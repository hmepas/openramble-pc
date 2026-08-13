namespace OpenRamble.AppCore;

/// <summary>
/// Keyboard event without the hook.
///
/// A gesture is a table "what happened + what came → what we are doing", and it has to be
/// checked as a table. A <c>KBDLLHOOKSTRUCT</c> cannot be assembled in a test, so parsing the
/// gesture works with this record and the hook turns its callback into one at the very edge.
/// </summary>
/// <param name="VirtualKey">The <c>vkCode</c> the low-level hook reported.</param>
/// <param name="IsDown">Key-down (<c>WM_KEYDOWN</c>/<c>WM_SYSKEYDOWN</c>) or key-up.</param>
/// <param name="HeldModifiers">
/// Every chord modifier down at this moment, the hotkey's own included. On macOS this was a
/// raw flags mask carrying keyboard side bits; Windows gives the side in
/// <paramref name="VirtualKey"/>, so only the kinds are needed here.
/// </param>
/// <param name="At">When the event happened.</param>
public readonly record struct HotkeyEvent(
    int VirtualKey,
    bool IsDown,
    HotkeyModifiers HeldModifiers,
    DateTimeOffset At);

public enum HotkeyActionKind
{
    None,

    /// <summary>The key is pressed - we begin dictation.</summary>
    Press,

    /// <summary>
    /// Let go - we finish. A short tap waits for the rest of the double-tap window;
    /// a long hold ends immediately.
    /// </summary>
    Release,

    /// <summary>Second press in a row - non-hold mode.</summary>
    DoubleTap,

    /// <summary>Pressing during recording without holding - please stop.</summary>
    StopHandsFree,

    /// <summary>
    /// While holding, another key was pressed: this is a shortcut, not dictation. The
    /// recording that has started must be interrupted quietly - without insertion, without
    /// messages and without opening a double-tap window.
    /// </summary>
    AbortShortcut,
}

/// <remarks>
/// The Swift original is an <c>enum</c> with one associated value on one case; a readonly
/// record struct keeps the value equality the tests are written against.
/// </remarks>
public readonly record struct HotkeyAction
{
    private HotkeyAction(HotkeyActionKind kind, TimeSpan releaseDelay)
    {
        Kind = kind;
        ReleaseDelay = releaseDelay;
    }

    public HotkeyActionKind Kind { get; }

    /// <summary>How long the stop waits for a possible second tap. Only for <see cref="HotkeyActionKind.Release"/>.</summary>
    public TimeSpan ReleaseDelay { get; }

    public static HotkeyAction None { get; } = new(HotkeyActionKind.None, TimeSpan.Zero);

    public static HotkeyAction Press { get; } = new(HotkeyActionKind.Press, TimeSpan.Zero);

    public static HotkeyAction DoubleTap { get; } = new(HotkeyActionKind.DoubleTap, TimeSpan.Zero);

    public static HotkeyAction StopHandsFree { get; } = new(HotkeyActionKind.StopHandsFree, TimeSpan.Zero);

    public static HotkeyAction AbortShortcut { get; } = new(HotkeyActionKind.AbortShortcut, TimeSpan.Zero);

    public static HotkeyAction Release(TimeSpan after) => new(HotkeyActionKind.Release, after);
}

/// <summary>
/// Hotkey gesture machine.
///
/// Remembers exactly two things: whether the key is pressed and when the last press was made.
/// Everything else is decided on the spot.
/// </summary>
/// <remarks>
/// One deviation from the macOS original, forced by the platform and only by it: there is a
/// single entry point. AppKit delivers modifier changes and regular key presses on two
/// separate monitors, so the original needed <c>handle(_:)</c> and <c>handleForeignKeyDown()</c>
/// side by side. <c>WH_KEYBOARD_LL</c> is one stream carrying every key including modifiers,
/// and splitting it back in two would invent a distinction the platform does not make. All
/// the original's decisions survive; the shortcut abort now arrives through
/// <see cref="Handle"/> as a foreign key-down.
/// </remarks>
public sealed class HotkeyGestureMachine
{
    private DateTimeOffset? lastTapAt;
    private DateTimeOffset? pressedAt;

    /// <summary>The current hold turned out to be a shortcut: releasing it means nothing.</summary>
    private bool abortedByShortcut;

    public HotkeyGestureMachine(DictationHotkey hotkey, TimeSpan? doubleTapWindow = null)
    {
        Hotkey = hotkey;
        DoubleTapWindow = doubleTapWindow ?? TimeSpan.FromSeconds(0.35);
    }

    public DictationHotkey Hotkey { get; private set; }

    /// <summary>
    /// Whether recording occurs without holding.
    ///
    /// The machine must know this in order to distinguish "start a new dictation" from
    /// "stop the current one": the gesture in both cases is the same - a press.
    /// </summary>
    public bool IsHandsFreeActive { get; set; }

    /// <summary>The interval within which the second press is considered a double press.</summary>
    public TimeSpan DoubleTapWindow { get; }

    public bool IsHeld { get; private set; }

    /// <summary>
    /// Change the key.
    ///
    /// Returns an action because a change in the middle of a hold must close the started
    /// gesture: events of the old key will not come at all after this, and there will be no
    /// way to release dictation - it would remain turned on forever.
    /// </summary>
    public HotkeyAction SetHotkey(DictationHotkey newHotkey)
    {
        bool wasHeld = IsHeld;
        Hotkey = newHotkey;
        IsHeld = false;
        lastTapAt = null;
        pressedAt = null;
        // In non-holding mode, releasing doesn't mean anything anyway: recording is in
        // progress until the next press, and it will come from the new key.
        return wasHeld && !IsHandsFreeActive ? HotkeyAction.Release(TimeSpan.Zero) : HotkeyAction.None;
    }

    /// <summary>Tracking stopped - the started gesture is interrupted.</summary>
    public void Reset()
    {
        IsHeld = false;
        lastTapAt = null;
        pressedAt = null;
        abortedByShortcut = false;
    }

    public HotkeyAction Handle(HotkeyEvent keyboardEvent)
    {
        if (keyboardEvent.VirtualKey != Hotkey.VirtualKey)
        {
            return HandleForeignKey(keyboardEvent);
        }

        if (keyboardEvent.IsDown && !IsHeld)
        {
            // Chord from the very beginning (Ctrl is already pressed) - does not count as a press.
            if (!IsExclusivelyPressed(keyboardEvent.HeldModifiers))
            {
                return HotkeyAction.None;
            }

            IsHeld = true;
            abortedByShortcut = false;
            pressedAt = keyboardEvent.At;

            if (IsHandsFreeActive)
            {
                lastTapAt = null;
                return HotkeyAction.StopHandsFree;
            }

            if (lastTapAt is DateTimeOffset previous && keyboardEvent.At - previous < DoubleTapWindow)
            {
                lastTapAt = null;
                return HotkeyAction.DoubleTap;
            }

            lastTapAt = keyboardEvent.At;
            return HotkeyAction.Press;
        }

        if (!keyboardEvent.IsDown && IsHeld)
        {
            IsHeld = false;
            if (abortedByShortcut)
            {
                // The shortcut has already interrupted the gesture: releasing does not insert
                // and does not open a double-tap window.
                abortedByShortcut = false;
                pressedAt = null;
                lastTapAt = null;
                return HotkeyAction.None;
            }

            TimeSpan heldFor = pressedAt is DateTimeOffset start
                ? keyboardEvent.At - start
                : DoubleTapWindow;
            pressedAt = null;
            // In non-hold mode, releasing does not mean anything: recording continues until
            // the next press.
            if (IsHandsFreeActive)
            {
                return HotkeyAction.None;
            }

            TimeSpan remaining = DoubleTapWindow - heldFor;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            // The hook does not promise sub-millisecond timestamps. Milliseconds are more than
            // sufficient for gestures and do not let floating point error lengthen or shorten
            // the window between identical gestures.
            return HotkeyAction.Release(TimeSpan.FromMilliseconds(Math.Round(remaining.TotalMilliseconds)));
        }

        // Auto-repeat while held, or a release of a key we never saw pressed.
        return HotkeyAction.None;
    }

    /// <summary>
    /// Someone else's key arrived while our own is held.
    ///
    /// A regular key (Ctrl+C) always ends the gesture. Another modifier ends it only if it
    /// turns the state into a chord: pressing the left Ctrl while the right one is held is
    /// still just Ctrl, and the person is not reaching for a shortcut.
    /// </summary>
    private HotkeyAction HandleForeignKey(HotkeyEvent keyboardEvent)
    {
        if (!IsHeld || abortedByShortcut || IsHandsFreeActive || !keyboardEvent.IsDown)
        {
            return HotkeyAction.None;
        }

        bool isModifier = IsModifierKey(keyboardEvent.VirtualKey);
        if (isModifier && IsExclusivelyPressed(keyboardEvent.HeldModifiers))
        {
            return HotkeyAction.None;
        }

        abortedByShortcut = true;
        lastTapAt = null;
        return HotkeyAction.AbortShortcut;
    }

    /// <summary>
    /// Whether the hotkey is held ALONE, without satellites.
    ///
    /// "Ctrl is already pressed, Alt has been added" - this is the path to the Ctrl+Alt+…
    /// shortcut, not to dictation. Counting such a chord as a press meant starting a
    /// background recording on every complex shortcut.
    /// </summary>
    private bool IsExclusivelyPressed(HotkeyModifiers held) => (held & ~Hotkey.Kind) == HotkeyModifiers.None;

    private static bool IsModifierKey(int virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => true, // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        0x11 or 0xA2 or 0xA3 => true, // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 => true, // VK_MENU, VK_LMENU, VK_RMENU
        0x5B or 0x5C => true,         // VK_LWIN, VK_RWIN
        _ => false,
    };
}
