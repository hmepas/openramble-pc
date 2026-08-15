namespace OpenRamble.AppCore;

/// <summary>
/// What a key is called in the numbers this platform's keyboard stream uses.
///
/// <see cref="DictationHotkey"/> names the keys a person can choose; nothing about that list is
/// platform-specific - it is a statement about PC keyboards. The numbers are another matter:
/// Windows delivers virtual-key codes through <c>WH_KEYBOARD_LL</c>, Linux delivers evdev
/// keycodes through <c>/dev/input</c>, and the same physical key wears a different number in
/// each. This interface is the whole of that difference, which is why it has two methods and no
/// state.
/// </summary>
/// <remarks>
/// <para>
/// The implementations are plain tables and live in this assembly on purpose. They carry no
/// platform API - a virtual-key code is a number whether or not <c>user32</c> exists - and
/// keeping them here means the Windows table is checked by the same test run as the Linux one,
/// on the Linux host that builds this repository. A table that only its own platform can verify
/// is a table nobody verifies.
/// </para>
/// <para>
/// The dependency rule is unchanged: the platform projects reference AppCore and pick the
/// implementation that matches their event source, and AppCore references neither of them.
/// </para>
/// </remarks>
public interface IHotkeyKeymap
{
    /// <summary>
    /// Code of a specific physical key.
    ///
    /// Needed to distinguish the right Ctrl from the left. On macOS the shared modifier flag made
    /// this a genuine hazard - "the right one was released while the left is still held" was
    /// indistinguishable from "nothing changed", and dictation stayed on forever. Both platforms
    /// ported to here give the side in the code itself, so the whole side-mask apparatus of the
    /// original is gone.
    /// </summary>
    int CodeFor(DictationHotkey hotkey);

    /// <summary>
    /// Whether this code belongs to a chord modifier - either side, all four kinds.
    /// </summary>
    /// <remarks>
    /// Asked of a key that arrived while the hotkey is held, and the answer decides whether the
    /// hold is still a dictation. A regular key means a shortcut; another modifier might only be
    /// the left twin of the key being held, which is not a shortcut at all. See
    /// <see cref="HotkeyGestureMachine"/>.
    /// </remarks>
    bool IsModifierCode(int keyCode);
}
