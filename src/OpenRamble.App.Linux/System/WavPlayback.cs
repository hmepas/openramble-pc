using System.ComponentModel;
using System.Diagnostics;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// Play one recording back, for the History tab.
///
/// The macOS original uses <c>AVAudioPlayer</c>; the freedesktop answer is whichever player the
/// audio server ships. <c>pw-play</c> comes with PipeWire, the same package the recorder is in,
/// so a machine that can dictate can play. <c>paplay</c> covers a PulseAudio session, and
/// <c>aplay</c> is the ALSA floor for a machine with neither.
/// </summary>
/// <remarks>
/// <para>
/// <b>One playback at a time.</b> Starting a second one stops the first: two takes over each
/// other is noise, and the History list has one play button per row for a reason.
/// </para>
/// <para>
/// <b>Stopping is killing the child.</b> None of these players has a control channel, and a
/// history take is seconds long; SIGKILL on a player is what a person means by "stop".
/// </para>
/// <para>
/// <b>A machine with no player plays nothing and says so once,</b> in the log. The audio is still
/// on disk and one click away through "Show in folder"; a dialog about a missing tool would be
/// louder than the recording.
/// </para>
/// </remarks>
public sealed class WavPlayback : IDisposable
{
    /// <summary>The players, in the order they are tried.</summary>
    internal static readonly IReadOnlyList<string> Players = ["pw-play", "paplay", "aplay"];

    private readonly Lock gate = new();
    private readonly Func<string, bool> isInstalled;
    private Process? playing;

    public WavPlayback()
        : this(DesktopTools.IsInstalled)
    {
    }

    internal WavPlayback(Func<string, bool> isInstalled)
    {
        this.isInstalled = isInstalled;
    }

    /// <summary>Whether a take is being played right now.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (gate)
            {
                try
                {
                    return playing is { HasExited: false };
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>The first player this machine has, or <c>null</c> when it has none.</summary>
    internal static string? ChoosePlayer(Func<string, bool> isInstalled)
    {
        foreach (string player in Players)
        {
            if (isInstalled(player)) { return player; }
        }

        return null;
    }

    /// <summary>
    /// Play a WAV from the beginning, stopping whatever was playing. <c>false</c> - nothing on
    /// this machine can play it.
    /// </summary>
    /// <param name="path">
    /// The file. Passed as one argument, never through a shell, so a name with a space in it stays
    /// one name - and it is never written to the log: it is a recording the person made.
    /// </param>
    public bool Play(string path)
    {
        Stop();

        string? player = ChoosePlayer(isInstalled);
        if (player is null)
        {
            DiagnosticLog.Emit("history.play", ("ok", false), ("error", "no-player"));
            return false;
        }

        var start = new ProcessStartInfo(player)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(path);

        try
        {
            Process? started = Process.Start(start);
            if (started is null) { return false; }

            // The pipes are drained so a chatty player cannot stall on a full one; nothing is kept.
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            lock (gate) { playing = started; }

            DiagnosticLog.Emit("history.play", ("ok", true), ("player", player));
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            DiagnosticLog.Emit("history.play", ("ok", false), ("player", player), ("error", error.Message));
            return false;
        }
    }

    /// <summary>End the playback, if there is one.</summary>
    public void Stop()
    {
        Process? child;
        lock (gate)
        {
            child = playing;
            playing = null;
        }

        if (child is null) { return; }

        try
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: false); }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception
            or NotSupportedException)
        {
            // Already gone, which is the outcome asked for.
        }
        finally
        {
            child.Dispose();
        }
    }

    public void Dispose() => Stop();
}
