using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DictationCore;
using DictationCore.Audio;

namespace OpenRamble.LinuxAudio;

/// <summary>
/// The recorder subprocess, as much of it as the capture is allowed to know.
/// </summary>
/// <remarks>
/// A seam of five members, and each one is a thing the capture must be able to do to a process
/// that has stopped cooperating: read what it has produced, ask it to leave, make it leave, find
/// out whether it did, and say why it died. With the real process behind it the stop deadline and
/// every failure path would only be observable on a machine with an audio server; behind this they
/// are ordinary tests.
/// </remarks>
internal interface IRecorderProcess : IDisposable
{
    /// <summary>The recorder's stdout: raw little-endian Float32, nothing else.</summary>
    Stream Output { get; }

    /// <summary>
    /// What the recorder said about its own death - exit status and the tail of its stderr.
    /// </summary>
    /// <remarks>
    /// The whole content of the failure a person is shown when a dictation produces nothing, so
    /// it has to be the recorder's own words: "Host is down" from a stopped audio server is the
    /// answer, and any sentence of ours in its place is a guess.
    /// </remarks>
    string ExitReport { get; }

    /// <summary>Ask it to stop (SIGTERM). Returns at once; it may be ignored.</summary>
    void RequestStop();

    /// <summary>Make it stop (SIGKILL). Returns at once; it cannot be ignored.</summary>
    void Kill();

    /// <summary>Wait for the process to be gone, never longer than <paramref name="budget"/>.</summary>
    bool WaitForExit(TimeSpan budget);
}

/// <summary>Starts a recorder. The one line of the capture that touches the operating system.</summary>
internal delegate IRecorderProcess RecorderProcessLauncher();

/// <summary>
/// <c>pw-record</c> streaming raw audio to a pipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subprocess.</b> The alternatives are P/Invoke into <c>libpipewire</c> - a callback API
/// with its own event loop thread, a per-distribution ABI and a segfault instead of an exception
/// when it is misused - or ALSA, which takes the device away from the desktop's audio server. The
/// tool ships with PipeWire itself, does the conversion to 16 kHz mono in the server's graph, and
/// its failures arrive as an exit status and a line of stderr, which is exactly the shape the
/// capture already reports. A process is also the only audio path that can be ended by force: the
/// stop budget below is enforceable here in a way no in-process audio callback can be.
/// </para>
/// <para>
/// <b>Absent is a first-class outcome.</b> A machine without PipeWire's command-line tools is
/// normal, and the failure has to name the missing thing and the package it lives in - a person
/// who is told "engine unavailable" learns nothing they can act on.
/// </para>
/// </remarks>
internal sealed class PwRecordProcess : IRecorderProcess
{
    /// <summary>The recorder, resolved through <c>PATH</c> the way a person would type it.</summary>
    public const string Executable = "pw-record";

    /// <summary>
    /// How much of stderr is kept for the failure message.
    /// </summary>
    /// <remarks>
    /// A recorder that fails says so in one line. The cap exists for the other case - a verbose
    /// build looping on a broken device - where an unbounded buffer would grow for as long as the
    /// dictation lasts.
    /// </remarks>
    private const int MaximumComplaintLength = 400;

    private const int SIGTERM = 15;

    private readonly Process process;
    private readonly Lock gate = new();
    private readonly StringBuilder complaint = new();

    private PwRecordProcess(Process process)
    {
        this.process = process;
        process.ErrorDataReceived += (_, args) => Remember(args.Data);
        process.BeginErrorReadLine();
    }

    public Stream Output => process.StandardOutput.BaseStream;

    public string ExitReport
    {
        get
        {
            string said;
            lock (gate) { said = complaint.ToString().Trim(); }

            bool exited;
            int code = 0;
            try
            {
                exited = process.HasExited;
                if (exited) { code = process.ExitCode; }
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                return said.Length > 0 ? said : $"{Executable} is gone";
            }

            string status = exited
                ? $"{Executable} exited with status {code}"
                : $"{Executable} closed its output while still running";
            return said.Length > 0 ? $"{status}: {said}" : status;
        }
    }

    /// <summary>
    /// The command line, in one place because it is the whole specification of the audio format.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>--rate</c>/<c>--channels</c>/<c>--format f32</c> - what recognition
    /// waits for, asked of the server rather than converted here. PipeWire resamples in its graph
    /// with the device's own clock; a resampler of ours on top of that would be a second one.</description></item>
    /// <item><description><c>--raw</c> and <c>-</c> - samples on stdout with no container. A WAV
    /// header on a stream that is never seeked back to has a length field that stays zero, and the
    /// capture writes its own take anyway.</description></item>
    /// <item><description><c>--latency 20ms</c> - against the 100 ms default, because
    /// <c>WaitForFirstFrame</c> is what the "speak" signal waits for and that default would be
    /// added to every dictation's first word. Twenty milliseconds is 320 samples a wakeup, which
    /// is nothing, and the pipe absorbs a stalled reader for a whole second.</description></item>
    /// <item><description><c>--media-role Communication</c> - what WASAPI's
    /// <c>Role.Communications</c> says on the other platform: this is a voice stream, duck the
    /// music.</description></item>
    /// <item><description><c>media.name</c> - the name the desktop's volume control shows next to
    /// the microphone that is live. A person who wants to know what is listening deserves better
    /// than "pw-record".</description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> Arguments(AudioStreamFormat format) =>
    [
        "--rate", ((int)format.SampleRate).ToString(CultureInfo.InvariantCulture),
        "--channels", format.Channels.ToString(CultureInfo.InvariantCulture),
        "--format", "f32",
        "--raw",
        "--latency", "20ms",
        "--media-role", "Communication",
        "-P", "media.name=\"OpenRamble dictation\"",
        "-",
    ];

    /// <summary>Start the recorder in the recording format, or say why there is none.</summary>
    /// <exception cref="AudioCaptureError.EngineUnavailable">
    /// The tool is missing, or the operating system refused to start it.
    /// </exception>
    public static IRecorderProcess Launch(AudioStreamFormat format)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // No shell: the arguments go to the process as they are written above, and a takes
            // directory with a space in it cannot become two arguments.
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in Arguments(format))
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or ObjectDisposedException)
        {
            throw new AudioCaptureError.EngineUnavailable(
                $"{Executable} could not be started ({error.Message}). "
                + "It comes with PipeWire's command-line tools - the `pipewire` package on Arch, "
                + "`pipewire-bin` on Debian and Ubuntu.");
        }

        if (process is null)
        {
            throw new AudioCaptureError.EngineUnavailable($"{Executable} could not be started");
        }

        return new PwRecordProcess(process);
    }

    public void RequestStop()
    {
        if (TrySignalTermination()) { return; }

        // No polite way to ask on this platform, or the signal did not go through. The recorder
        // is a child process of ours with a pipe nobody will read again; leaving it running is
        // not one of the outcomes.
        Kill();
    }

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or Win32Exception)
        {
            // Already gone, which is the outcome asked for.
        }
    }

    public bool WaitForExit(TimeSpan budget)
    {
        try
        {
            int milliseconds = (int)Math.Clamp(budget.TotalMilliseconds, 0, int.MaxValue);
            return process.WaitForExit(milliseconds);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // There is no process to wait for: it was never started, or it has already been
            // reaped. Both mean "gone", which is what the caller is waiting to hear.
            return true;
        }
    }

    public void Dispose() => process.Dispose();

    /// <summary>
    /// SIGTERM, which <c>pw-record</c> answers by closing its stream and leaving.
    /// </summary>
    /// <remarks>
    /// .NET has no way to send a signal other than <c>Kill</c>, which is SIGKILL on Unix. The
    /// difference is worth one P/Invoke: SIGKILL leaves the audio server to notice a dead client
    /// on its own, and does it while the last frames are still in the pipe.
    /// </remarks>
    private bool TrySignalTermination()
    {
        if (!OperatingSystem.IsLinux()) { return false; }

        try
        {
            if (process.HasExited) { return true; }

            return SendSignal(process.Id, SIGTERM) == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or EntryPointNotFoundException
            or DllNotFoundException)
        {
            return false;
        }
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) { return; }

        lock (gate)
        {
            if (complaint.Length >= MaximumComplaintLength) { return; }

            if (complaint.Length > 0) { complaint.Append(" | "); }

            complaint.Append(line.Trim());
        }
    }

    /// <remarks>
    /// <c>DllImport</c> rather than the generated <c>LibraryImport</c>: the source generator
    /// requires <c>AllowUnsafeBlocks</c> for the whole assembly, and two blittable integers are
    /// not worth turning that on in the one project that touches the operating system.
    /// </remarks>
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);
}
