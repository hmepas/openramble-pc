using System.Runtime.InteropServices;
using System.Text;
using DictationCore;
using LocalASR;
using OpenRamble.AppCore;

namespace OpenRamble.Cli;

/// <summary>
/// <c>openramble AUDIO_FILE...</c>: upstream's <c>apps/macos/OpenRambleCLI/main.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// Not ported: <c>--hebrew</c>. This port ships no Hebrew model, and an option that can only
/// answer "not installed" is not one to offer. <c>WAI_MODELS_ROOT</c> is
/// <c>OPENRAMBLE_MODELS_ROOT</c> here, the name asr-bench already reads.
/// </para>
/// <para>
/// Exit codes are upstream's: 0 every file transcribed, 1 some file failed or was left
/// unchanged, 64 a usage error, 69 no usable model, 70 anything else, 130 interrupted.
/// </para>
/// </remarks>
public static class CommandLineTool
{
    internal const string Usage = """
        Usage: openramble AUDIO_FILE [--format txt|json|srt|vtt]
               openramble AUDIO_FILE... --output-dir DIRECTORY [--format FORMAT]
               openramble --check-model

        Uses the installed Parakeet model offline, even when the app is closed.
        Results go to stdout or separate files in --output-dir; progress goes to stderr.
        Existing output files are never overwritten. One failed input does not stop others.
        Long jobs yield while OpenRamble is dictating. Ctrl-C stops the job safely.
        WAV is read directly; other audio and video files need ffmpeg installed.

        Options:
          --format FORMAT        txt (default), json, srt or vtt
          --output-dir DIRECTORY write separate transcripts here
          --model-dir DIRECTORY  use a prepared folder containing the GGUF model
          --check-model          check model availability without loading it
          --                     treat remaining arguments as file paths
          -h, --help             show this help

        Environment:
          OPENRAMBLE_MODELS_ROOT  model install root (the app's data folder by default)
        """;

    /// <summary>
    /// Whether these arguments are for the command-line tool rather than the app: any at all,
    /// except the app's own <paramref name="appFlags"/>.
    /// </summary>
    public static bool Handles(IReadOnlyList<string> arguments, params string[] appFlags) =>
        arguments.Count > 0 && !appFlags.Contains(arguments[0], StringComparer.Ordinal);

    /// <summary>Run the tool to completion and return its exit code.</summary>
    public static int Run(IReadOnlyList<string> arguments)
    {
        Options options;
        try
        {
            options = Options.Parse(arguments);
        }
        catch (CliError error)
        {
            Diagnostic(error.Message);
            return error.Code;
        }

        using var cancellation = new CancellationTokenSource();
        Action<PosixSignalContext> stop = context =>
        {
            // Stop at the next piece rather than now: completed output files stay, and no
            // temporary file is left behind.
            context.Cancel = true;
            cancellation.Cancel();
        };
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, stop);
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stop);
        try
        {
            return Run(options, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Diagnostic("Interrupted. Completed output files have been kept.");
            return 130;
        }
        catch (CliError error)
        {
            Diagnostic(error.Message);
            return error.Code;
        }
        catch (Exception error)
        {
            Diagnostic($"Transcription failed: {error.Message}");
            return 70;
        }
    }

    private static async Task<int> Run(Options options, CancellationToken cancellationToken)
    {
        if (options.Help)
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        AppPaths paths = AppPaths.Standard();
        string directory = ResolveEngineDirectory(options.ModelDirectory, Environment.GetEnvironmentVariable("OPENRAMBLE_MODELS_ROOT") ?? paths.Models());
        if (options.CheckModel)
        {
            Diagnostic("Model is installed and available.");
            if (options.Files.Count == 0) { return 0; }
        }

        if (options.OutputDirectory is { } output) { Directory.CreateDirectory(output); }

        int failures = 0;
        var jobs = new List<Job>();
        foreach (Job job in Jobs(options.Files, options.OutputDirectory, options.Format))
        {
            if (job.Output is { } destination && Path.Exists(destination))
            {
                Diagnostic($"Already exists, left unchanged: {destination}");
                failures++;
            }
            else
            {
                jobs.Add(job);
            }
        }

        if (jobs.Count == 0) { return 1; }

        using var priority = new DictationPriority(paths.DictationPriorityLock());
        await priority.WaitForTurn(cancellationToken).ConfigureAwait(false);
        var transcriber = new LocalTranscriber(MakeEngine());
        try
        {
            await transcriber.Prepare(directory).ConfigureAwait(false);
            await new FileTranscriber(transcriber, priority).Transcribe(
                [.. jobs.Select(job => job.Input)],
                (index, outcome) =>
                {
                    Job job = jobs[index];
                    try
                    {
                        FileTranscript transcript = outcome switch
                        {
                            FileOutcome.Transcribed transcribed => transcribed.Transcript,
                            FileOutcome.Failed failed => throw failed.Error,
                            _ => throw new InvalidOperationException(),
                        };
                        if (job.Output is { } destination)
                        {
                            options.Format.Write(transcript, destination);
                            Diagnostic($"Saved: {destination}");
                        }
                        else
                        {
                            using Stream stdout = Console.OpenStandardOutput();
                            stdout.Write(options.Format.Render(transcript));
                            stdout.Flush();
                        }
                    }
                    catch (Exception error)
                    {
                        failures++;
                        Diagnostic($"Failed: {job.Input}: {error.Message}");
                    }

                    return Task.CompletedTask;
                },
                (index, seconds, total) => Diagnostic(
                    $"Transcribing {index + 1}/{jobs.Count}: {(int)Math.Min(100, seconds / Math.Max(total, 0.001) * 100)}%"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transcriber.Unload().ConfigureAwait(false);
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>One input and where its transcript goes: a file, or standard output when <c>null</c>.</summary>
    internal sealed record Job(string Input, string? Output);

    /// <summary>
    /// Each input's transcript is named after it. Two inputs with the same name - in different
    /// folders, or differing only in case or Unicode composition - get <c>-2</c>, <c>-3</c>, ...
    /// so neither replaces the other on any file system.
    /// </summary>
    internal static List<Job> Jobs(IReadOnlyList<string> files, string? outputDirectory, TranscriptFormat format)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new List<Job>();
        foreach (string file in files)
        {
            // A dot-file such as ".recording" has no extension to drop, as Foundation's URL sees it.
            string stem = Path.GetFileNameWithoutExtension(file) is { Length: > 0 } bare ? bare : Path.GetFileName(file);
            string name = $"{stem}.{format.Name}";
            int suffix = 2;
            while (!names.Add(name.Normalize(NormalizationForm.FormC).ToLowerInvariant()))
            {
                name = $"{stem}-{suffix}.{format.Name}";
                suffix++;
            }

            jobs.Add(new Job(file, outputDirectory is null ? null : Path.Combine(outputDirectory, name)));
        }

        return jobs;
    }

    /// <summary>
    /// The engine folder to load, checked without touching the installation: the app may be
    /// installing or repairing the model right now (<see cref="ModelStore.InspectInstalledState"/>).
    /// </summary>
    internal static string ResolveEngineDirectory(string? explicitDirectory, string modelsRoot)
    {
        if (explicitDirectory is not null)
        {
            int models = Directory.Exists(explicitDirectory)
                ? Directory.EnumerateFiles(explicitDirectory)
                    .Count(path => string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
                : 0;
            if (models != 1)
            {
                throw new CliError(69, "The model directory must contain exactly one readable GGUF model.");
            }

            return explicitDirectory;
        }

        (ModelManifest manifest, ModelManifest? previousManifest) = Manifests();
        var layout = new ModelInstallLayout(manifest, modelsRoot);
        ModelState state = new ModelStore(manifest, layout, NoDownloads.Instance).InspectInstalledState();
        // The app keeps the model the current one replaced until the new one is ready; the
        // command line uses it in the meantime, exactly as the app does.
        if (!state.IsReady && previousManifest is not null)
        {
            var previousLayout = new ModelInstallLayout(previousManifest, modelsRoot);
            ModelState previous = new ModelStore(previousManifest, previousLayout, NoDownloads.Instance).InspectInstalledState();
            if (new ModelLineup(state, previous).Recognizer == ModelLineup.Slot.Previous)
            {
                return previousLayout.EngineDirectory;
            }
        }

        return state switch
        {
            ModelState.Ready => layout.EngineDirectory,
            ModelState.NotInstalled => throw new CliError(69, "Model is not installed. Open OpenRamble and install it in Settings."),
            ModelState.RepairRequired repair => throw new CliError(
                69,
                $"Model is incomplete or damaged: {repair.Reason}. Finish installation or repair it in OpenRamble Settings."),
            _ => throw new CliError(69, "Model is unavailable. Check OpenRamble Settings."),
        };
    }

    /// <summary>What each app serves: on Linux Parakeet Ultra, keeping Parakeet v3 until Ultra is in.</summary>
    private static (ModelManifest Current, ModelManifest? Previous) Manifests() =>
        OperatingSystem.IsWindows()
            ? (ModelManifest.Bundled(), null)
            : (ModelManifest.BundledUltra(), ModelManifest.Bundled());

    private static IASREngineAdapting MakeEngine() =>
        OperatingSystem.IsWindows()
            ? new SherpaOnnxAdapter()
            : new EngineByModelFiles(new TranscribeCppAdapter(), new SherpaOnnxAdapter());

    private static void Diagnostic(string text) => Console.Error.WriteLine(text);

    /// <summary>The command line never downloads: a model comes from the app.</summary>
    private sealed class NoDownloads : IModelDownloading
    {
        public static readonly NoDownloads Instance = new();

        public Task<string> Download(Uri url, long expectedBytes, Action<long> onProgress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the command-line tool does not download models");
    }
}

/// <summary>A message for standard error and the exit code that goes with it.</summary>
internal sealed class CliError(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>The parsed command line.</summary>
internal sealed record Options
{
    public List<string> Files { get; } = [];

    public string? ModelDirectory { get; private set; }

    public string? OutputDirectory { get; private set; }

    public TranscriptFormat Format { get; private set; } = TranscriptFormat.Txt;

    public bool CheckModel { get; private set; }

    public bool Help { get; private set; }

    public static Options Parse(IReadOnlyList<string> arguments)
    {
        var options = new Options();
        bool positional = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (positional)
            {
                options.Files.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--help" or "-h":
                    options.Help = true;
                    break;
                case "--":
                    positional = true;
                    break;
                case "--check-model":
                    options.CheckModel = true;
                    break;
                case "--model-dir":
                    options.ModelDirectory = Value(argument);
                    break;
                case "--output-dir":
                    options.OutputDirectory = Value(argument);
                    break;
                case "--format":
                    string name = Value(argument);
                    options.Format = TranscriptFormats.Parse(name) ?? throw new CliError(64, $"Unknown format: {name}");
                    break;
                default:
                    if (argument.StartsWith('-')) { throw new CliError(64, $"Unknown option: {argument}"); }
                    options.Files.Add(argument);
                    break;
            }

            string Value(string option)
            {
                index++;
                return index < arguments.Count && arguments[index].Length > 0
                    ? arguments[index]
                    : throw new CliError(64, $"{option} requires a value");
            }
        }

        if (!options.Help && !options.CheckModel && options.Files.Count == 0)
        {
            throw new CliError(64, CommandLineTool.Usage);
        }

        if (options.Files.Count > 1 && options.OutputDirectory is null)
        {
            throw new CliError(64, "Use --output-dir for multiple files so each keeps its own transcript.");
        }

        return options;
    }
}
