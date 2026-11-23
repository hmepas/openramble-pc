using System.ComponentModel;
using System.Diagnostics;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// Starting one of the desktop's command-line tools and forgetting about it.
/// </summary>
/// <remarks>
/// <para>
/// The shell reaches the desktop through three of them - <c>xdg-open</c> for a folder,
/// <c>canberra-gtk-play</c> for the attention sound, and nothing else - and all three are
/// fire-and-forget: nothing here reads their output, and none of them is on a path a dictation
/// waits for. The insertion adapter has its own runner (<c>SubprocessCommandRunner</c> in
/// OpenRamble.LinuxInput) because it needs the opposite of this: a deadline, standard input that
/// never becomes an argument, and the tool's own words when it fails.
/// </para>
/// <para>
/// <b>A missing tool is an ordinary outcome, not an error.</b> A machine with no
/// <c>xdg-open</c> is unusual; one with no <c>libcanberra</c> is not. Both answer the same way -
/// <c>false</c>, and a line in the log if anybody asked for one.
/// </para>
/// </remarks>
internal static class DesktopTools
{
    /// <summary>Run a tool without waiting for it. <c>false</c> - it is not on this machine.</summary>
    internal static bool Start(string executable, params string[] arguments)
    {
        // The absolute path, so the working directory is never searched for the tool.
        string? resolved = DictationCore.ExecutablePath.Find(executable);
        if (resolved is null)
        {
            DiagnosticLog.Emit("tool.missing", ("tool", executable), ("error", "not found on PATH"));
            return false;
        }

        var start = new ProcessStartInfo(resolved) { UseShellExecute = false };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }

        try
        {
            using Process? started = Process.Start(start);
            return started is not null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            DiagnosticLog.Emit("tool.missing", ("tool", executable), ("error", error.Message));
            return false;
        }
    }

    /// <summary>
    /// Whether a tool is on <c>PATH</c> at all, without running it.
    /// </summary>
    /// <remarks>
    /// The one question that must be answerable without side effects: whether the microphone can
    /// be recorded from is asked once a second, and the honest probe for it opens the device -
    /// which is exactly what a person who is not dictating must never have happen. See
    /// <see cref="LinuxMicrophoneAccess"/>.
    /// </remarks>
    internal static bool IsInstalled(string executable) =>
        DictationCore.ExecutablePath.Find(executable) is not null;
}
