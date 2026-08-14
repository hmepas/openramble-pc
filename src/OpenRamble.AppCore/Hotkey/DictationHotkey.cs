namespace OpenRamble.AppCore;

/// <summary>
/// The key to start dictation.
///
/// The macOS original offers four modifiers - Fn, Right Command, Right Option, Left
/// Control - chosen because they can be held and do not take regular combinations away
/// from applications. Windows needs the same property from different keys, so the list is
/// recast rather than translated:
///
/// <list type="bullet">
/// <item><description><b>Fn has no Windows counterpart at all.</b> On PC keyboards Fn is
/// resolved in the keyboard's own firmware and never reaches the operating system: no
/// hook, no message, nothing to bind. It is dropped, and with it the whole
/// <c>AppleFnUsageType</c> question.</description></item>
/// <item><description><b>Right Command → Right Ctrl.</b> The default. A modifier nobody
/// presses alone, present on every keyboard, and distinguishable from its left twin by
/// virtual-key code.</description></item>
/// <item><description><b>Right Shift</b> has no macOS counterpart in the original list and is
/// added here, because Windows has a failure mode macOS does not: a virtual-machine client, a
/// remote desktop or a keyboard remapper can eat one particular key on its way to the
/// application, and Right Ctrl is a favourite. Right Shift is a modifier nobody presses alone,
/// present on every keyboard, free of any action of its own, and - the point - a different
/// physical key. Without it, everything left to fall back on either composes characters,
/// shouts, or is absent from the keyboard.</description></item>
/// <item><description><b>Right Option → Right Alt,</b> with a real caveat: on layouts that
/// have AltGr (most European ones, including the Russian typographic layouts this product
/// is dictated in) Right Alt *is* AltGr and composes characters. See
/// <see cref="HotkeyAdvice"/>.</description></item>
/// <item><description><b>Left Control → Caps Lock.</b> Left Ctrl is the busiest key on a PC
/// keyboard - Ctrl+C, Ctrl+V, Ctrl+Z - and offering it as a hold-to-dictate key would fire
/// the abort path several times a minute. Caps Lock is the Windows key nobody needs, and a
/// low-level hook can swallow its toggle.</description></item>
/// <item><description><b>F13</b> is added: no shipping keyboard sends it by accident, no
/// application binds it, and it is what keyboard remappers and macro keys emit. The cost is
/// that most keyboards cannot produce it at all.</description></item>
/// </list>
/// </summary>
public enum DictationHotkey
{
    RightCtrl,
    RightShift,
    RightAlt,
    CapsLock,
    F13,
}

/// <summary>
/// The modifier kinds a chord can be built from.
///
/// Sides are deliberately merged here: this set answers "is another modifier down", and for
/// that question left and right Ctrl are the same key. Telling the hotkey itself apart from
/// its twin is <see cref="DictationHotkeyInfo.VirtualKey"/>'s job, and on Windows that is
/// exact - unlike macOS, where the shared modifier flag hid the release of the right key.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}

/// <summary>What each hotkey candidate is, in the terms the gesture machine needs.</summary>
public static class DictationHotkeyInfo
{
    /// <summary>Virtual-key codes, from <c>winuser.h</c>.</summary>
    private const int VkRightControl = 0xA3;
    private const int VkRightShift = 0xA1;
    private const int VkRightMenu = 0xA5;
    private const int VkCapital = 0x14;
    private const int VkF13 = 0x7C;

    /// <summary>
    /// The picker's order, and it is an order: the keys that just work come first, and the two
    /// that carry a warning come last.
    /// </summary>
    public static IReadOnlyList<DictationHotkey> AllCases { get; } =
    [
        DictationHotkey.RightCtrl,
        DictationHotkey.RightShift,
        DictationHotkey.RightAlt,
        DictationHotkey.CapsLock,
        DictationHotkey.F13,
    ];

    extension(DictationHotkey hotkey)
    {
        public string Title => hotkey switch
        {
            DictationHotkey.RightCtrl => "Right Ctrl",
            DictationHotkey.RightShift => "Right Shift",
            DictationHotkey.RightAlt => "Right Alt",
            DictationHotkey.CapsLock => "Caps Lock",
            DictationHotkey.F13 => "F13",
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey)),
        };

        /// <summary>
        /// Code of a specific physical key.
        ///
        /// Needed to distinguish the right Ctrl from the left. On macOS the shared modifier
        /// flag made this a genuine hazard - "the right one was released while the left is
        /// still held" was indistinguishable from "nothing changed", and dictation stayed on
        /// forever. Windows delivers the side in the virtual-key code itself, so the whole
        /// side-mask apparatus of the original is gone.
        /// </summary>
        public int VirtualKey => hotkey switch
        {
            DictationHotkey.RightCtrl => VkRightControl,
            DictationHotkey.RightShift => VkRightShift,
            DictationHotkey.RightAlt => VkRightMenu,
            DictationHotkey.CapsLock => VkCapital,
            DictationHotkey.F13 => VkF13,
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey)),
        };

        /// <summary>
        /// Which chord modifier this key raises while it is held.
        ///
        /// <see cref="HotkeyModifiers.None"/> for keys that are not modifiers at all: holding
        /// Caps Lock or F13 adds nothing to the chord state, so any other modifier held at the
        /// same time is a shortcut in progress.
        /// </summary>
        public HotkeyModifiers Kind => hotkey switch
        {
            DictationHotkey.RightCtrl => HotkeyModifiers.Control,
            DictationHotkey.RightShift => HotkeyModifiers.Shift,
            DictationHotkey.RightAlt => HotkeyModifiers.Alt,
            DictationHotkey.CapsLock => HotkeyModifiers.None,
            DictationHotkey.F13 => HotkeyModifiers.None,
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey)),
        };

        /// <summary>
        /// Whether the hotkey's own key events must be swallowed while dictation runs.
        ///
        /// Caps Lock toggles on key-down: without suppression every dictation would leave the
        /// keyboard shouting. Right Alt is AltGr on composing layouts, where swallowing it
        /// would break typing instead - the warning in <see cref="HotkeyAdvice"/> is the
        /// honest answer there, not suppression.
        /// </summary>
        public bool NeedsSuppression => hotkey == DictationHotkey.CapsLock;
    }
}
