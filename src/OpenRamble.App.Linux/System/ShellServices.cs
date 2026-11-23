using DictationCore;
using OpenRamble.AppCore;
using OpenRamble.LinuxInput;

namespace OpenRamble.App.Linux;

/// <summary>
/// A clipboard write that stays on this machine.
/// </summary>
/// <remarks>
/// Delegates to <see cref="LinuxTextInserter"/> rather than driving <c>wl-copy</c> itself, exactly
/// as the Windows one delegates to its inserter: the dictation must leave the clipboard the same
/// way whichever path put it there, and two copies of that code are two chances for one of them to
/// drift. What "stays on this machine" is worth here is written down in
/// <see cref="LinuxClipboardRestrictions"/> - less than on Windows, and honestly so.
/// </remarks>
public sealed class HostOnlyClipboard : IHostOnlyClipboard
{
    private readonly LinuxTextInserter inserter;

    public HostOnlyClipboard(LinuxTextInserter inserter)
    {
        this.inserter = inserter;
    }

    public void Copy(string text) => inserter.CopyForPerson(text);
}

/// <summary>Show a file or folder to the person in their file manager.</summary>
/// <remarks>
/// <para>
/// The freedesktop file manager interface (<c>org.freedesktop.FileManager1</c>, which Dolphin,
/// Nautilus, Nemo and Thunar answer on the session bus) rather than <c>xdg-open</c>: a file is
/// shown selected in its folder, as "Show in Finder" does, and the folder is opened by a file
/// manager. <c>xdg-open</c> hands a folder to whatever claims <c>inode/directory</c>, which on
/// plenty of desktops is a terminal or an editor - that opens nothing worth looking at.
/// </para>
/// <para>
/// <c>busctl</c> (systemd) first, <c>gdbus</c> (GLib) if it is missing; <c>xdg-open</c> on the
/// folder only when no file manager answered. The call waits for the file manager to start, so
/// it runs off the caller's thread.
/// </para>
/// </remarks>
public sealed class FolderOpening : IFolderOpening
{
    private const string Service = "org.freedesktop.FileManager1";
    private const string ObjectPath = "/org/freedesktop/FileManager1";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly Func<string, string[], bool> run;
    private readonly Action<string, string[]> start;

    public FolderOpening()
        : this((tool, arguments) => DesktopTools.Run(tool, Deadline, arguments),
               (tool, arguments) => DesktopTools.Start(tool, arguments))
    {
    }

    /// <param name="run">Runs a tool and waits: <c>true</c> - it succeeded.</param>
    /// <param name="start">Starts a tool without waiting.</param>
    internal FolderOpening(Func<string, string[], bool> run, Action<string, string[]> start)
    {
        this.run = run;
        this.start = start;
    }

    public void Open(string path) => _ = Task.Run(() => Reveal(path));

    internal void Reveal(string path)
    {
        string full = Path.GetFullPath(path);
        bool isFolder = Directory.Exists(full);
        string method = isFolder ? "ShowFolders" : "ShowItems";
        string uri = FileUri(full);

        if (run("busctl", ["--user", "call", Service, ObjectPath, Service, method, "ass", "1", uri, ""])) { return; }
        if (run("gdbus", ["call", "--session", "--dest", Service, "--object-path", ObjectPath,
            "--method", Service + "." + method, $"['{uri}']", ""])) { return; }

        start("xdg-open", [isFolder ? full : Path.GetDirectoryName(full) ?? full]);
    }

    /// <summary>A <c>file://</c> URI with every path segment percent-encoded (spaces, Cyrillic, quotes).</summary>
    internal static string FileUri(string absolutePath) =>
        "file://" + string.Join('/', absolutePath.Split('/').Select(Uri.EscapeDataString));
}

/// <summary>
/// The one sound the product has: "look at the panel".
/// </summary>
/// <remarks>
/// <para>
/// macOS plays "Submarine", Windows plays the shell's information sound; the freedesktop
/// counterpart is the sound theme's <c>dialog-information</c>, played through
/// <c>canberra-gtk-play</c>. Same reasoning as on the other two platforms: a theme sound follows
/// whatever the person has chosen in their sound settings - including having chosen silence - and
/// it is not the error tone, because what happened is recoverable and the text is still offered in
/// the menu.
/// </para>
/// <para>
/// <b>A machine without libcanberra makes no sound, and that is not a failure worth reporting.</b>
/// The sound is a nudge towards a panel that is already on screen saying the same thing in words;
/// a dialog box explaining that a nudge could not be played would be louder than the nudge.
/// </para>
/// </remarks>
public sealed class SoundPlayer : ISounding
{
    /// <summary>The freedesktop sound theme's information sound, by its specified name.</summary>
    internal const string SoundId = "dialog-information";

    private const string Player = "canberra-gtk-play";

    public Task PlayAttention()
    {
        DesktopTools.Start(Player, "--id", SoundId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Whether the active keyboard layout composes characters with Right Alt.
/// </summary>
/// <remarks>
/// <para>
/// Windows asks the layout itself, key by key. Nothing on Wayland answers this: the layout belongs
/// to the compositor, no protocol publishes it to an ordinary application, and the X11 answer read
/// through XWayland is whatever the compositor last pushed there - right on some desktops, stale
/// on others.
/// </para>
/// <para>
/// So the answer is <c>true</c>, and it is a decision rather than a guess. Right Alt composes on
/// every layout but plain <c>us</c>, the cost of being wrong is one warning too many on a screen a
/// person visits twice, and the cost of the other mistake is a person losing their <c>@</c> and
/// their <c>€</c> with nothing on screen having warned them. It is the same rule
/// <see cref="KeyUsage.Unknown"/> is already decided by: where the answer cannot be had, warn.
/// </para>
/// </remarks>
public static class KeyboardLayoutFacts
{
    public static bool HasAltGraph() => true;
}
