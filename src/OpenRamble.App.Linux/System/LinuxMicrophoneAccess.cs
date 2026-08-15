using DictationCore;
using OpenRamble.AppCore;
using OpenRamble.LinuxAudio;

namespace OpenRamble.App.Linux;

/// <summary>
/// Whether this machine will let the application record, and what to do when it will not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Linux has no consent store, so both ports collapse into one type.</b> Windows has two
/// separate places to read - a device switch and a per-user one - and a Settings page to send a
/// person to. Here there is nothing to read and nothing to ask: whether the microphone can be
/// opened is decided by the sound server and by the <c>audio</c> group, and the only way to learn
/// it is to open a device and see. That makes the answer to "is it granted" and the act of
/// requesting it the same fact, and splitting them across two objects would mean two caches of it.
/// </para>
/// <para>
/// <b>The probe is not run on the poll.</b> <see cref="IPermissionReading.MicrophoneGranted"/> is
/// read once a second for the life of the process, and a probe opens the microphone: polling with
/// it would light the desktop's recording indicator all day for a person who is not dictating, and
/// on a machine where <c>pw-record</c> takes half a second to come up it would also be a process
/// per second. So the poll reads what is already known, and what is already known starts as the
/// cheap half of the question - whether the recorder is installed at all - and is replaced by the
/// truth the first time a real probe or a real dictation runs.
/// </para>
/// <para>
/// <b>There is no settings page, and <see cref="OpenSettings"/> says so by doing nothing.</b> The
/// remedies for a failed probe are starting PipeWire, plugging a microphone in, or joining a
/// group - none of which is a page any desktop agrees on the name of. This is the same shape of
/// missing answer as <see cref="HotkeyAdvice.LinuxSettingsHint"/>. The state machine in
/// <see cref="AppState"/> still moves to
/// <see cref="MicrophoneConsentState.WaitingForSettings"/> after a refused request - the state is
/// "we asked and were told no", which is true here - and what the person is told there is
/// <see cref="MicrophoneRemedy.Linux"/>: check the device, then try again.
/// </para>
/// </remarks>
public sealed class LinuxMicrophoneAccess : IPermissionReading, IMicrophoneConsentFlow
{
    /// <summary>
    /// How long the probe waits for the first frame of sound.
    /// </summary>
    /// <remarks>
    /// Long enough for a cold PipeWire graph to route a new stream, short enough that a person who
    /// pressed "Grant" is not left watching a button. A probe that times out is reported as a
    /// refusal, which is what it is: the device did not deliver.
    /// </remarks>
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(3);

    private readonly Action activate;
    private readonly Func<string> takesDirectory;

    /// <summary>
    /// The last thing known about the microphone. Written by the probe, read by the poll.
    /// </summary>
    private volatile bool granted;

    /// <param name="activate">Bring our own windows back after something else took the focus.</param>
    /// <param name="takesDirectory">
    /// Where the probe's throwaway take would go. The recording is discarded before this method
    /// returns - the capture deletes its own file on an abort - but it has to be written somewhere
    /// while it exists, and it must be the same folder every other recording uses so that a
    /// read-only application directory fails here too rather than at the first dictation.
    /// </param>
    public LinuxMicrophoneAccess(Action activate, Func<string> takesDirectory)
    {
        this.activate = activate;
        this.takesDirectory = takesDirectory;
        granted = DesktopTools.IsInstalled(PipeWireCapture.RecorderExecutable);
    }

    public bool MicrophoneGranted => granted;

    /// <summary>
    /// Open the microphone, wait for a moment of sound, close it.
    /// </summary>
    /// <remarks>
    /// The honest test, and the same one the person's next dictation would perform anyway. It is
    /// deliberately not "does the device exist": a device that is there and busy, a sound server
    /// that is not running and a user who is not in the right group all present as a working
    /// device right up to the moment a stream is asked for.
    /// </remarks>
    /// <remarks>
    /// On a worker, like the Windows one: starting the recorder is a process launch and the caller
    /// is the interface thread that has just drawn a pressed button.
    /// </remarks>
    public async Task<bool> Request()
    {
        bool heard = await Task.Run(Probe).ConfigureAwait(false);
        granted = heard;
        return heard;
    }

    /// <inheritdoc cref="LinuxMicrophoneAccess"/>
    public void OpenSettings() => DiagnosticLog.Emit("microphone.settings", ("opened", false));

    public void ActivateApplication() => activate();

    private async Task<bool> Probe()
    {
        PipeWireCapture capture;
        try
        {
            capture = new PipeWireCapture(takesDirectory());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.EmitException("microphone.probe", error, ("heard", false));
            return false;
        }

        try
        {
            _ = await capture.StartRecording().ConfigureAwait(false);
            Task<bool> first = capture.WaitForFirstFrame();
            // A recorder that dies without ever answering leaves the wait outstanding; a probe
            // that never returns would leave the button pressed forever.
            Task finished = await Task.WhenAny(first, Task.Delay(ProbeBudget)).ConfigureAwait(false);
            bool heard = ReferenceEquals(finished, first) && first.Result;
            DiagnosticLog.Emit("microphone.probe", ("heard", heard), ("timed_out", !ReferenceEquals(finished, first)));
            return heard;
        }
        catch (AudioCaptureError error)
        {
            DiagnosticLog.EmitException("microphone.probe", error, ("heard", false));
            return false;
        }
        finally
        {
            await capture.AbortRecording().ConfigureAwait(false);
            capture.Dispose();
        }
    }
}
