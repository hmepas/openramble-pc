using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DictationCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// Which kind of application has focus, where the desktop is willing to say.
/// </summary>
/// <remarks>
/// <para>
/// The text pipeline shapes a take for the app it is going to: paragraphs in prose, verbatim in a
/// terminal, and a real Return for "Send it." only in a terminal. All it needs is the app's
/// identifier - the window class, which <see cref="PromptModePolicy"/> looks up where macOS looks up
/// a bundle id. Never the window title: that carries file names and mail subjects, and nothing
/// here needs them.
/// </para>
/// <para>
/// Wayland gives an ordinary client no way to ask, so the answer comes from the compositor where
/// it has one: Hyprland answers on its control socket (what <c>hyprctl activewindow -j</c> reads),
/// and a plain X11 session answers through <c>xprop</c>. Everywhere else - GNOME, KDE, Sway, or a
/// compositor that does not answer in time - the answer is "unknown", and unknown is verbatim with
/// no Return: the safe default, never a guess.
/// </para>
/// </remarks>
internal static class FocusedWindow
{
    /// <summary>
    /// How long the compositor or the tool may take. The question is asked at key-down, so it has
    /// to be cheap; a desktop that cannot answer quickly is treated as one that cannot answer.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>The focused window, or <c>null</c> when this desktop does not say.</summary>
    public static TargetApplication? Query(ICommandRunner runner)
    {
        if (Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 } signature)
        {
            return Hyprland(signature);
        }

        // Only a session with no Wayland at all: under XWayland, xprop sees X clients only and
        // would name whichever of them last had focus.
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not { Length: > 0 }
            && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
        {
            return X11(runner);
        }

        return null;
    }

    /// <summary>The answer to Hyprland's <c>j/activewindow</c>.</summary>
    internal static TargetApplication? FromHyprland(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("class", out JsonElement windowClass)
                || windowClass.ValueKind != JsonValueKind.String
                || windowClass.GetString() is not { Length: > 0 } name)
            {
                return null;
            }

            int pid = root.TryGetProperty("pid", out JsonElement process) && process.TryGetInt32(out int value)
                ? value
                : 0;
            return new TargetApplication(name, pid, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What <c>xprop -id … WM_CLASS _NET_WM_PID</c> printed. The class is the second string of
    /// <c>WM_CLASS</c>, the one XWayland windows carry as their class on Hyprland too.
    /// </summary>
    internal static TargetApplication? FromXprop(string window)
    {
        Match windowClass = Regex.Match(window, "WM_CLASS\\(STRING\\) = \"[^\"]*\", \"([^\"]+)\"");
        if (!windowClass.Success) { return null; }
        Match pid = Regex.Match(window, "_NET_WM_PID\\(CARDINAL\\) = (\\d+)");
        return new TargetApplication(
            windowClass.Groups[1].Value,
            pid.Success && int.TryParse(pid.Groups[1].Value, out int value) ? value : 0,
            null);
    }

    /// <summary>The window id in <c>xprop -root _NET_ACTIVE_WINDOW</c>, or <c>null</c> for none.</summary>
    internal static string? ActiveWindowId(string root)
    {
        Match id = Regex.Match(root, "#\\s*(0x[0-9a-fA-F]+)");
        return id.Success && id.Groups[1].Value != "0x0" ? id.Groups[1].Value : null;
    }

    private static TargetApplication? Hyprland(string signature)
    {
        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
        // Hyprland moved its sockets from /tmp into the runtime directory; either may be live.
        foreach (string directory in new[] { Path.Combine(runtime, "hypr", signature), Path.Combine("/tmp/hypr", signature) })
        {
            string path = Path.Combine(directory, ".socket.sock");
            if (!File.Exists(path)) { continue; }
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                {
                    SendTimeout = (int)Budget.TotalMilliseconds,
                    ReceiveTimeout = (int)Budget.TotalMilliseconds,
                };
                socket.Connect(new UnixDomainSocketEndPoint(path));
                socket.Send("j/activewindow"u8);
                var answer = new MemoryStream();
                byte[] buffer = new byte[4096];
                int read;
                while ((read = socket.Receive(buffer)) > 0) { answer.Write(buffer, 0, read); }
                return FromHyprland(Encoding.UTF8.GetString(answer.ToArray()));
            }
            catch (SocketException)
            {
                // A stale socket, or a compositor that did not answer in time: unknown.
            }
        }

        return null;
    }

    private static TargetApplication? X11(ICommandRunner runner)
    {
        CommandResult root = runner.Run(new CommandRequest("xprop", ["-root", "_NET_ACTIVE_WINDOW"], Budget: Budget));
        if (!root.Succeeded || ActiveWindowId(root.Output) is not { } id) { return null; }
        CommandResult window = runner.Run(new CommandRequest("xprop", ["-id", id, "WM_CLASS", "_NET_WM_PID"], Budget: Budget));
        return window.Succeeded ? FromXprop(window.Output) : null;
    }
}
