using System.ComponentModel;
using System.Diagnostics;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>Runs one program to completion and returns its exit code.</summary>
public delegate Task<int> ProcessRunner(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// The audio a person actually sends someone (upstream <c>MeetingAudioExporter</c>).
/// </summary>
/// <remarks>
/// A recording is kept as 16 kHz stereo (or its 96 kbit/s archive) so the two sides stay
/// separable; the export is AAC at 32 kbit/s in an M4A container, mixed to mono so either voice
/// plays in both ears. <c>ffmpeg</c> does the work, resolved on <c>PATH</c> the safe way
/// (<see cref="ExecutablePath"/>); its stereo-to-mono downmix averages the two channels, so
/// simultaneous voices do not clip. A failed or cancelled export leaves nothing behind: a
/// half-written file that plays ten minutes of a two-hour meeting is worse than none.
/// </remarks>
public sealed class MeetingAudioExporter
{
    public const string BitRate = "32k";

    private readonly string? ffmpeg;
    private readonly ProcessRunner run;

    /// <param name="searchPath">Where to look for <c>ffmpeg</c>; <c>null</c> - <c>PATH</c>.</param>
    public MeetingAudioExporter(string? searchPath = null)
        : this(ExecutablePath.Find("ffmpeg", searchPath), RunProcess)
    {
    }

    /// <param name="ffmpeg">The resolved <c>ffmpeg</c>, or <c>null</c> when there is none.</param>
    /// <param name="run">How it is run; tests pass a fake.</param>
    public MeetingAudioExporter(string? ffmpeg, ProcessRunner run)
    {
        this.ffmpeg = ffmpeg;
        this.run = run;
    }

    public bool IsAvailable => ffmpeg is not null;

    /// <summary>The whole command line after the program name.</summary>
    public static IReadOnlyList<string> Arguments(string source, string destination) =>
    [
        "-nostdin", "-v", "error", "-threads", "1", "-y",
        "-protocol_whitelist", "file",
        "-i", "file:" + Path.GetFullPath(source),
        "-vn", "-ac", "1", "-c:a", "aac", "-b:a", BitRate, "-f", "ipod",
        "file:" + Path.GetFullPath(destination),
    ];

    /// <summary>
    /// Encode <paramref name="source"/> to a mono M4A at <paramref name="destination"/>. False when
    /// it cannot; cancellation throws and, like a failure, removes whatever was written.
    /// </summary>
    public async Task<bool> Export(string source, string destination, CancellationToken cancellationToken = default)
    {
        if (ffmpeg is null || !File.Exists(source)) { return false; }

        int exitCode;
        try
        {
            exitCode = await run(ffmpeg, Arguments(source, destination), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Discard(destination);
            throw;
        }
        catch (Win32Exception)
        {
            exitCode = -1;
        }

        if (exitCode == 0 && File.Exists(destination)) { return true; }
        Discard(destination);
        return false;
    }

    private static void Discard(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The real runner: no shell, no input, output drained so the pipe never fills.</summary>
    public static async Task<int> RunProcess(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }

        using Process process = Process.Start(start) ?? throw new Win32Exception("ffmpeg did not start");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        await Task.WhenAll(output, error).ConfigureAwait(false);
        return process.ExitCode;
    }
}
