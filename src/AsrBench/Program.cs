using System.Diagnostics;
using System.Globalization;
using DictationCore;
using LocalASR;
using OpenRamble.AppCore.Audio;
#if WINDOWS
using OpenRamble.WindowsAudio;
#else
using OpenRamble.LinuxAudio;
#endif

namespace AsrBench;

/// <summary>
/// Phase P0 tool: install the model, check it, recognize and measure.
///
/// It exists for the sake of one gate - "the engine is chosen on live speech, not on
/// synthesis". It is the only way to run real recordings through the shipping pipeline and
/// get quality, speed and memory numbers out of it.
/// </summary>
internal static class Program
{
    private static int Usage()
    {
        Console.WriteLine(
            """
            asr-bench - testing local recognition

            Commands:
              status                what is installed and where
              install               download and install the model (~670 MB)
              import <folder>       take the model from a ready folder, without network
              delete                delete the installed model
              transcribe <file>...  recognize files
              bench <file>...       recognize and measure speed and memory
              timings <file>...     recognize and print word timings
              eval <manifest.json>  run a corpus and compute WER/CER
              mic [options]         record from the microphone, then recognize (Windows, Linux)

            mic options:
              --seconds <n>         how long to record (default 5)
              --out <file>          where to keep the take (default take.wav)
              --no-transcribe       record and report only, skip recognition

            Environment variables:
              OPENRAMBLE_MODELS_ROOT     installation root (%LOCALAPPDATA%\OpenRamble\Models default)
              OPENRAMBLE_MODEL_DIR       engine folder past the storage (for measurements)
              OPENRAMBLE_ASR_PRECISION   int8 (default) | float32
              OPENRAMBLE_ASR_PROVIDER    cpu (default) | cuda | directml
              OPENRAMBLE_ASR_THREADS     intra-op threads (default: product value, 4)
              OPENRAMBLE_ASR_DECODING    greedy_search (default) | modified_beam_search
              OPENRAMBLE_ASR_LANGUAGE    forced language (ru, en, ...) instead of auto
              OPENRAMBLE_ASR_PREWARM=on  run the same inference warm-up as the shipping app
              OPENRAMBLE_EVAL_PIPELINE   on | exact - score the text after the replacement dictionary
            """);
        return 64;
    }

    private static async Task<int> Main(string[] arguments)
    {
        // Windows consoles default to an OEM code page that cannot print
        // Cyrillic - recognized text came out as "???". Harmless elsewhere.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached (redirected output); the default is fine.
        }

        if (arguments.Length == 0)
        {
            return Usage();
        }

        string command = arguments[0];
        string[] operands = arguments[1..];

        return command switch
        {
            "status" => Status(),
            "install" => await Install().ConfigureAwait(false),
            "import" => operands.Length == 0 ? Usage() : await Import(operands[0]).ConfigureAwait(false),
            "delete" => await Delete().ConfigureAwait(false),
            "eval" => operands.Length == 0 ? Usage() : await Eval(operands[0]).ConfigureAwait(false),
            "mic" => await Mic(operands).ConfigureAwait(false),
            "timings" => operands.Length == 0 ? Usage() : await Timings(operands).ConfigureAwait(false),
            "transcribe" or "bench" => operands.Length == 0
                ? Usage()
                : await Transcribe(operands, measure: command == "bench").ConfigureAwait(false),
            _ => Usage(),
        };
    }

    /// <summary>
    /// Record from the microphone. Present only where there is one this tool can open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only line of this tool that knows which platform it is on. The Windows build records
    /// through WASAPI and the portable build through <c>pw-record</c>; both are the very library
    /// the shipping application records with, which is the whole point of the command. A build
    /// that has neither says so rather than pretending the command does not exist, because the
    /// person reading the usage text on the wrong machine deserves the reason.
    /// </para>
    /// <para>
    /// The capture is built before the operands are parsed, which is safe because building one
    /// opens nothing: both implementations take a directory and touch the device only at
    /// <c>StartRecording</c>.
    /// </para>
    /// </remarks>
    private static async Task<int> Mic(string[] operands)
    {
        string takes = Path.Combine(Path.GetTempPath(), "openramble-bench-takes");
#if WINDOWS
        using IMicrophoneDevice capture = new WasapiCapture(takes);
#else
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine(
                "mic: the microphone commands exist in the Windows and Linux builds of asr-bench.");
            return 69;
        }

        using IMicrophoneDevice capture = new PipeWireCapture(takes);
#endif
        return await MicCommand.Run(operands, capture).ConfigureAwait(false);
    }

    // MARK: - Storage

    private static string? ModelsRoot() => Environment.GetEnvironmentVariable("OPENRAMBLE_MODELS_ROOT");

    private static (ModelStore Store, ModelInstallLayout Layout, ModelManifest Manifest) MakeStore()
    {
        ModelManifest manifest = ModelManifest.Bundled();
        var layout = new ModelInstallLayout(manifest, ModelsRoot());
        var store = new ModelStore(manifest, layout, new HttpClientModelDownloader());
        return (store, layout, manifest);
    }

    private static string FormatBytes(long bytes) =>
        (bytes / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Peak process memory - for the "the model fits into memory" gate.</summary>
    private static long PeakMemoryBytes()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PeakWorkingSet64;
    }

    private static void PrintState(ModelState state, ModelInstallLayout layout, ModelManifest manifest)
    {
        switch (state)
        {
            case ModelState.NotInstalled:
                Console.WriteLine("Model not installed.");
                Console.WriteLine(
                    $"Will download: {FormatBytes(manifest.TotalByteCount)} into {layout.InstalledDirectory}");
                break;
            case ModelState.Downloading downloading:
                Console.WriteLine(
                    $"Loading: {FormatBytes(downloading.ReceivedBytes)} of {FormatBytes(downloading.TotalBytes)}");
                break;
            case ModelState.Verifying verifying:
                Console.WriteLine($"Checking: {verifying.Checked} of {verifying.Total}");
                break;
            case ModelState.Ready ready:
                Console.WriteLine($"Model ready: {ready.Directory}");
                Console.WriteLine($"Revision: {manifest.Revision}");
                Console.WriteLine(
                    $"Files: {manifest.Files.Count}, {FormatBytes(manifest.TotalByteCount)}");
                break;
            case ModelState.RepairRequired repair:
                Console.WriteLine($"Model requires recovery: {repair.Reason}");
                break;
            case ModelState.Failed failed:
                Console.WriteLine($"Error: {failed.Error.Message}");
                break;
            case ModelState.Deleting:
                Console.WriteLine("Deleting...");
                break;
        }
    }

    private static int Status()
    {
        (ModelStore store, ModelInstallLayout layout, ModelManifest manifest) = MakeStore();
        ModelState state = store.RefreshState();
        PrintState(state, layout, manifest);

        // The acoustic term hinter has no ONNX publication, so there is no second store to
        // report on. Saying so beats printing nothing.
        Console.WriteLine();
        try
        {
            _ = ModelManifest.BundledVocabulary();
        }
        catch (ModelManifestError.EngineUnsupported error)
        {
            Console.WriteLine($"Term hint: unavailable - {error.Reason}");
        }

        return state.IsReady ? 0 : 69;
    }

    /// <summary>Downloading with progress.</summary>
    private static async Task<int> Install()
    {
        (ModelStore store, ModelInstallLayout layout, ModelManifest manifest) = MakeStore();
        if (store.RefreshState().IsReady)
        {
            Console.WriteLine($"Model already installed: {layout.InstalledDirectory}");
            return 0;
        }

        Console.WriteLine(
            $"Downloading {FormatBytes(manifest.TotalByteCount)} - {manifest.Files.Count} file(s)");
        Console.WriteLine($"Source: {manifest.Repository} @ {manifest.Revision}");

        using var monitorStop = new CancellationTokenSource();
        Task monitor = Task.Run(
            async () =>
            {
                int lastShown = -1;
                await foreach (ModelState state in store.States(monitorStop.Token).ConfigureAwait(false))
                {
                    switch (state)
                    {
                        case ModelState.Downloading downloading when downloading.TotalBytes > 0:
                            int percent = (int)(downloading.ReceivedBytes * 100.0 / downloading.TotalBytes);
                            if (percent >= lastShown + 5)
                            {
                                lastShown = percent;
                                Console.WriteLine(
                                    $"  {percent}% - {FormatBytes(downloading.ReceivedBytes)}");
                            }

                            break;
                        case ModelState.Verifying verifying:
                            Console.WriteLine($"  check {verifying.Checked}/{verifying.Total}");
                            break;
                    }
                }
            },
            monitorStop.Token);

        await store.Install().ConfigureAwait(false);
        await monitorStop.CancelAsync().ConfigureAwait(false);
        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The monitor is a printer, not a step of the install.
        }

        ModelState final = store.CurrentState();
        PrintState(final, layout, manifest);
        return final.IsReady ? 0 : 70;
    }

    private static async Task<int> Import(string source)
    {
        (ModelStore store, ModelInstallLayout layout, ModelManifest manifest) = MakeStore();
        if (store.RefreshState().IsReady)
        {
            Console.WriteLine($"Model already installed: {layout.InstalledDirectory}");
            return 0;
        }

        Console.WriteLine($"Taking the model from {source}");
        Console.WriteLine(
            $"Will check all {manifest.Files.Count} checksums - the source of trust does not change");

        await store.ImportModel(source).ConfigureAwait(false);
        ModelState imported = store.CurrentState();
        PrintState(imported, layout, manifest);
        return imported.IsReady ? 0 : 70;
    }

    private static async Task<int> Delete()
    {
        (ModelStore store, ModelInstallLayout layout, _) = MakeStore();
        await store.Delete().ConfigureAwait(false);
        Console.WriteLine($"Deleted: {layout.ModelDirectory}");
        return 0;
    }

    // MARK: - The engine

    private static bool IsOn(string name) =>
        string.Equals(
            Environment.GetEnvironmentVariable(name),
            "on",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An unknown value is a visible error, never a silent fall back to the default: otherwise
    /// the measurement would quietly measure something other than what the command says.
    /// </summary>
    private static string? Choice(string variable, params string[] allowed)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        if (raw is null)
        {
            return null;
        }

        if (!allowed.Contains(raw, StringComparer.Ordinal))
        {
            Console.WriteLine($"{variable}: unknown option '{raw}'");
            Environment.Exit(64);
        }

        Console.WriteLine($"{variable}: {raw}");
        return raw;
    }

    internal static async Task<LocalTranscriber> PrepareTranscriber()
    {
        // An explicit engine folder is the only way to compare two file sets: the storage
        // checks the installation against the manifest and rightly rejects an extra file.
        string? explicitDirectory = Environment.GetEnvironmentVariable("OPENRAMBLE_MODEL_DIR");

        string engineDirectory;
        if (explicitDirectory is not null)
        {
            Console.WriteLine($"Engine folder given explicitly: {explicitDirectory} (frozen, past the storage)");
            engineDirectory = explicitDirectory;
        }
        else
        {
            (ModelStore store, ModelInstallLayout layout, _) = MakeStore();
            if (!store.RefreshState().IsReady)
            {
                Console.WriteLine("The model is not installed. Run: asr-bench install");
                Environment.Exit(69);
            }

            engineDirectory = layout.EngineDirectory;
        }

        EncoderPrecision precision = Choice("OPENRAMBLE_ASR_PRECISION", "int8", "float32") == "float32"
            ? EncoderPrecision.Float32
            : EncoderPrecision.Int8;
        string provider = Choice("OPENRAMBLE_ASR_PROVIDER", "cpu", "cuda", "directml") ?? "cpu";
        string decoding =
            Choice("OPENRAMBLE_ASR_DECODING", "greedy_search", "modified_beam_search")
            ?? SherpaOnnxAdapter.DefaultDecodingMethod;

        // The thread count is not repeated as a number here: the default lives in the adapter,
        // and the bench must measure exactly what the product ships. The variable only
        // overrides it.
        int threads = SherpaOnnxAdapter.DefaultNumThreads;
        if (Environment.GetEnvironmentVariable("OPENRAMBLE_ASR_THREADS") is { } rawThreads)
        {
            if (!int.TryParse(rawThreads, CultureInfo.InvariantCulture, out threads) || threads <= 0)
            {
                Console.WriteLine($"OPENRAMBLE_ASR_THREADS: expected a positive number, received '{rawThreads}'");
                Environment.Exit(64);
            }

            Console.WriteLine($"Threads: {threads}");
        }

        var adapter = new SherpaOnnxAdapter(precision, threads, provider, decoding);
        var transcriber = new LocalTranscriber(adapter);
        long started = Stopwatch.GetTimestamp();
        await transcriber.Prepare(engineDirectory).ConfigureAwait(false);
        Console.WriteLine(
            $"Model loaded in {Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");

        if (IsOn("OPENRAMBLE_ASR_PREWARM"))
        {
            long warmupStarted = Stopwatch.GetTimestamp();
            await transcriber.WarmUpInference().ConfigureAwait(false);
            Console.WriteLine(
                $"Inference warmed in {Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
        }

        return transcriber;
    }

    /// <summary>
    /// The application's replacement dictionary on top of the raw response is what a person
    /// actually sees. <c>OPENRAMBLE_EVAL_PIPELINE=on</c> turns it on: the raw answer is
    /// printed next to it, the scorer counts the processed one.
    /// </summary>
    /// <remarks>
    /// Swift builds a whole <c>TextPipeline</c>. That type is not ported yet, so what runs
    /// here is its dictionary stage alone - which is exactly what the flag is named after.
    /// The polisher is deliberately left out: it capitalizes and re-spaces, and the scorer
    /// folds case and separates punctuation anyway.
    /// </remarks>
    private static Func<string, string>? MakeEvalPipeline()
    {
        string? mode = Environment.GetEnvironmentVariable("OPENRAMBLE_EVAL_PIPELINE")?.ToLowerInvariant();
        switch (mode)
        {
            case "on":
                Console.WriteLine("The scorer counts the text after the replacement dictionary (OPENRAMBLE_EVAL_PIPELINE=on)");
                return text => DictionaryReplacements.Apply(StarterDictionary.Developer, text);
            case "exact":
                Console.WriteLine("Dictionary without phonetic matching (OPENRAMBLE_EVAL_PIPELINE=exact)");
                return text => DictionaryReplacements.Apply(
                    StarterDictionary.Developer,
                    text,
                    phoneticMatching: false);
            default:
                return null;
        }
    }

    /// <summary>
    /// A forced language instead of auto-detection - exactly what a person picks in settings.
    /// It exists so the hypothesis "a hard language choice helps" can be checked by running
    /// rather than by discussing.
    /// </summary>
    internal static string? LanguageHint()
    {
        string? raw = Environment.GetEnvironmentVariable("OPENRAMBLE_ASR_LANGUAGE");
        if (raw is null)
        {
            return null;
        }

        if (!SherpaOnnxAdapter.SupportedLanguageHints.Contains(raw))
        {
            Console.WriteLine($"OPENRAMBLE_ASR_LANGUAGE: unsupported language '{raw}'");
            Environment.Exit(64);
        }

        Console.WriteLine($"Forced language: {raw}");
        Console.WriteLine(
            "  (the ONNX engine has no language input: the hint is validated and the model still auto-detects)");
        return raw;
    }

    // MARK: - Recognition

    private static async Task<int> Eval(string manifestPath)
    {
        IReadOnlyList<EvalItem> items = Evaluation.LoadManifest(manifestPath);
        LocalTranscriber transcriber = await PrepareTranscriber().ConfigureAwait(false);
        Func<string, string>? pipeline = MakeEvalPipeline();
        string? language = LanguageHint();

        var outcomes = new List<EvalOutcome>();
        foreach (EvalItem item in items)
        {
            long started = Stopwatch.GetTimestamp();
            ASRResult result;
            try
            {
                result = await transcriber.Transcribe(item.File, language).ConfigureAwait(false);
            }
            catch (ASREngineError error)
            {
                Console.WriteLine($"\n=== {Path.GetFileName(item.File)} ===");
                Console.WriteLine($"Error: {error.Message}");
                continue;
            }

            string hypothesis = pipeline is null ? result.Text : pipeline(result.Text);
            var outcome = new EvalOutcome(
                item,
                TranscriptScorer.Score(item.Reference, hypothesis),
                result,
                Stopwatch.GetElapsedTime(started),
                PeakMemoryBytes());
            outcomes.Add(outcome);
            Console.WriteLine(Evaluation.Describe(outcome, showDifferences: true));
        }

        Console.WriteLine(Evaluation.Summary(outcomes));
        Console.WriteLine($"\nprocess peak memory: {FormatBytes(PeakMemoryBytes())}");
        return 0;
    }

    private static async Task<int> Timings(string[] paths)
    {
        LocalTranscriber transcriber = await PrepareTranscriber().ConfigureAwait(false);
        foreach (string path in paths)
        {
            ASRResult result = await transcriber.Transcribe(path).ConfigureAwait(false);
            Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "audio {0:F2} s, words {1}",
                result.AudioDuration.TotalSeconds,
                result.Words.Count));
            foreach (ASRResult.Word word in result.Words)
            {
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,8:F2} {1,8:F2}  {2}",
                    word.Start.TotalSeconds,
                    word.End.TotalSeconds,
                    word.Text));
            }
        }

        return 0;
    }

    private static async Task<int> Transcribe(string[] paths, bool measure)
    {
        LocalTranscriber transcriber = await PrepareTranscriber().ConfigureAwait(false);
        string? language = LanguageHint();

        foreach (string path in paths)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                ASRResult result = await transcriber.Transcribe(path, language).ConfigureAwait(false);
                TimeSpan wall = Stopwatch.GetElapsedTime(started);

                Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
                Console.WriteLine(result.Text);

                if (measure)
                {
                    double realtimeFactor = wall > TimeSpan.Zero ? result.AudioDuration / wall : 0;
                    Console.WriteLine("---");
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "audio: {0:F2} s",
                        result.AudioDuration.TotalSeconds));
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "recognized: {0:F2} s",
                        wall.TotalSeconds));
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:F1} times faster than real time",
                        realtimeFactor));
                    Console.WriteLine($"words with timings: {result.Words.Count}");
                    Console.WriteLine($"peak memory: {FormatBytes(PeakMemoryBytes())}");
                }
            }
            catch (ASREngineError error)
            {
                Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
                Console.WriteLine($"Error: {error.Message}");
                return 70;
            }
        }

        return 0;
    }
}
