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

/// <summary>Show a folder to the person.</summary>
/// <remarks>
/// <c>xdg-open</c> rather than a named file manager: which one is installed is the desktop's
/// business, and the tool exists to answer exactly that question.
/// </remarks>
public sealed class FolderOpening : IFolderOpening
{
    public void Open(string path) => DesktopTools.Start("xdg-open", path);
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
