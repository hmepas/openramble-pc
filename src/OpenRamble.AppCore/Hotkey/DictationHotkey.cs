namespace OpenRamble.AppCore;

/// <summary>
/// The key to start dictation.
///
/// The macOS original offers four modifiers - Fn, Right Command, Right Option, Left
/// Control - chosen because they can be held and do not take regular combinations away
/// from applications. A PC keyboard needs the same property from different keys, so the list is
/// recast rather than translated. Every reason below is a fact about the keyboard and about what
/// people type on it, not about the operating system reading it, which is why the list is the
/// same on Windows and on Linux and only the codes underneath differ - see
/// <see cref="IHotkeyKeymap"/>:
///
/// <list type="bullet">
/// <item><description><b>Fn has no PC counterpart at all.</b> On PC keyboards Fn is
/// resolved in the keyboard's own firmware and never reaches the operating system: no
/// hook, no message, nothing to bind. It is dropped, and with it the whole
/// <c>AppleFnUsageType</c> question.</description></item>
/// <item><description><b>Right Command → Right Ctrl.</b> The default. A modifier nobody
/// presses alone, present on every keyboard, and distinguishable from its left twin by
/// key code.</description></item>
/// <item><description><b>Right Shift</b> has no macOS counterpart in the original list and is
/// added here, because a PC has a failure mode macOS does not: a virtual-machine client, a
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
/// the abort path several times a minute. Caps Lock is the PC key nobody needs, and both
/// platforms can swallow its toggle - see <c>NeedsSuppression</c> below.
/// </description></item>
/// <item><description><b>F13 was offered and is not any more.</b> Nothing binds it and nothing
/// sends it by accident - and nothing sends it at all: it needs a remapper or a macro key, which
/// makes it a choice that cannot be tried out on the machine where it is being chosen. A picker
/// row that most people cannot test is a row that costs attention and returns nothing. The case
/// stays in this enum only so a settings file written by an earlier build still parses; see
/// <see cref="DictationHotkeyInfo.AllCases"/>, which no longer offers it.</description></item>
/// </list>
/// </summary>
public enum DictationHotkey
{
    RightCtrl,
    RightShift,
    RightAlt,
    CapsLock,

    /// <summary>
    /// The left-hand twins, added for the same reason upstream added Left Command and Left Option
    /// in 0.15.0: every key that types nothing while held is offered. They also answer a Linux
    /// fact the right-hand keys cannot: keyd, a common remapper, folds Right Ctrl and Right Shift
    /// into their left codes on its virtual keyboard, so on such a machine the right keys never
    /// arrive and the left ones are the only Ctrl and Shift there are.
    /// </summary>
    LeftCtrl,
    LeftShift,

    /// <summary>Not offered. Kept so a stored choice from an earlier build is still readable.</summary>
    F13,
}

/// <summary>
/// The modifier kinds a chord can be built from.
///
/// Sides are deliberately merged here: this set answers "is another modifier down", and for
/// that question left and right Ctrl are the same key. Telling the hotkey itself apart from
/// its twin is <see cref="IHotkeyKeymap.CodeFor"/>'s job, and on both ported platforms that is
/// exact - unlike macOS, where the shared modifier flag hid the release of the right key.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,

    /// <summary>
    /// The key between Ctrl and Alt: Win on a PC keyboard, Super to X11 and Wayland, Meta to
    /// evdev. Named for the label people read, not for the system reading the key.
    /// </summary>
    Windows = 1 << 3,
}

/// <summary>
/// What each hotkey candidate is, in the terms the gesture machine needs.
/// </summary>
/// <remarks>
/// Everything here is true of the key itself on any desktop; the one fact that is not - the
/// number the operating system calls the key by - lives in <see cref="IHotkeyKeymap"/>.
/// </remarks>
public static class DictationHotkeyInfo
{
    /// <summary>
    /// What the pickers offer, and it is an order: the keys that just work come first - right
    /// side, then left - and the two that carry a warning come last.
    /// </summary>
    /// <remarks>
    /// <see cref="DictationHotkey.F13"/> is deliberately absent. It is the only candidate a
    /// person cannot try on the keyboard in front of them - it takes a remapper to send at all -
    /// and a row that cannot be tested where it is chosen teaches nothing. Reading a stored
    /// choice checks it against this list, so a settings file that still says <c>F13</c> falls
    /// back to the default rather than leaving the key silent.
    /// </remarks>
    public static IReadOnlyList<DictationHotkey> AllCases { get; } =
    [
        DictationHotkey.RightCtrl,
        DictationHotkey.RightShift,
        DictationHotkey.LeftCtrl,
        DictationHotkey.LeftShift,
        DictationHotkey.RightAlt,
        DictationHotkey.CapsLock,
    ];

    extension(DictationHotkey hotkey)
    {
        public string Title => hotkey switch
        {
            DictationHotkey.RightCtrl => "Right Ctrl",
            DictationHotkey.RightShift => "Right Shift",
            DictationHotkey.RightAlt => "Right Alt",
            DictationHotkey.CapsLock => "Caps Lock",
            DictationHotkey.LeftCtrl => "Left Ctrl",
            DictationHotkey.LeftShift => "Left Shift",
            DictationHotkey.F13 => "F13",
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
            DictationHotkey.LeftCtrl => HotkeyModifiers.Control,
            DictationHotkey.LeftShift => HotkeyModifiers.Shift,
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
        /// <remarks>
        /// Stays here rather than in <see cref="IHotkeyKeymap"/> because it is a fact about the
        /// key and not about the platform: Caps Lock shouts wherever it is pressed. How the
        /// swallowing is done does differ - a low-level hook returns 1 on Windows, an exclusive
        /// evdev grab withholds the event on Linux - but that is the event source's business, and
        /// neither platform is in a position to answer "should we" differently.
        /// </remarks>
        public bool NeedsSuppression => hotkey == DictationHotkey.CapsLock;
    }
}
