using System.Globalization;

namespace OpenRamble.AppCore;

/// <summary>
/// A shortcut in the ordinary desktop sense: a key, with modifiers held down.
///
/// Separate from <see cref="DictationHotkey"/>, which is deliberately a <i>bare</i> modifier
/// you hold. The two do different jobs and the difference is not cosmetic: hold-to-talk needs
/// a key that can be held for a sentence without typing anything, so it can only be a
/// modifier. Copying is a press, and a press wants a chord - binding it to a bare Control would
/// fire every time the person reached for Control on the way to something else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform-neutral by construction.</b> The key code is whatever number the platform's
/// keyboard stream uses - a virtual-key code from <c>WH_KEYBOARD_LL</c> on Windows, an evdev
/// keycode on Linux - exactly as <see cref="IHotkeyKeymap"/> already does for the dictation
/// key. The modifiers are <see cref="HotkeyModifiers"/>, the same four kinds the gesture
/// machine reads, with sides merged: a chord does not care which Ctrl was held. Everything that
/// depends on which platform the number came from - which codes are the function row, what is
/// printed on the key - lives behind <see cref="IShortcutKeymap"/>, so this type can be stored,
/// compared and matched on any host and tested on the Linux box that builds both.
/// </para>
/// <para>
/// The macOS original keeps only the four modifier bits of the event's whole mask, because
/// arrow keys raise <c>.function</c> on their own and Caps Lock raises its bit whenever it
/// happens to be on. The same discipline holds here: <see cref="HotkeyModifiers"/> has no bit
/// for Caps Lock or Num Lock, so a shortcut recorded with either engaged matches with either
/// off. The platform monitor is what folds lock keys away before asking <see cref="Matches"/>.
/// </para>
/// </remarks>
public readonly record struct KeyCombination
{
    public KeyCombination(int keyCode, HotkeyModifiers modifiers)
    {
        KeyCode = keyCode;
        Modifiers = modifiers & AllowedModifiers;
    }

    public int KeyCode { get; }

    /// <summary>
    /// Only the four modifiers a shortcut is built from, and nothing else - see the type
    /// remarks for why lock keys are deliberately not represented.
    /// </summary>
    public HotkeyModifiers Modifiers { get; }

    /// <summary>Everything a shortcut may be built from, and nothing else.</summary>
    public const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows;

    // MARK: - What may be bound

    /// <summary>
    /// The function row stands alone; everything else needs a modifier.
    ///
    /// A bare letter would fire in the middle of a word, and Shift with a letter is just a
    /// capital letter - both would make the app type into someone's document by accident.
    /// F13 pressed by itself types nothing, which is exactly why people reach for it.
    /// </summary>
    /// <param name="keymap">Which codes are the function row is a platform fact.</param>
    public bool IsValid(IShortcutKeymap keymap)
    {
        if (keymap.IsFunctionKey(KeyCode)) { return true; }
        return (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows))
            != HotkeyModifiers.None;
    }

    /// <summary>
    /// Whether this combination is what the person just pressed.
    ///
    /// Compares the modifier set exactly rather than "contains": Ctrl+C must not answer to
    /// Ctrl+Shift+C, which in most applications means something else.
    /// </summary>
    public bool Matches(int keyCode, HotkeyModifiers pressed) =>
        keyCode == KeyCode && (pressed & AllowedModifiers) == Modifiers;

    // MARK: - How it reads

    /// <summary>
    /// The shortcut as a PC shows it: <c>Ctrl+Alt+C</c>.
    ///
    /// The macOS original prints glyphs in Apple's order (⌃⌥⇧⌘) so it reads like every menu
    /// on the machine; a PC menu spells the words out and joins them with <c>+</c>, in the
    /// order Windows itself uses - Ctrl, Alt, Shift, Win - so this reads like every menu here.
    /// The key's own label is asked of <paramref name="keymap"/>, for the reason the original
    /// asks the keyboard layout: the shortcut lives on the physical key, not on the letter.
    /// </summary>
    public string DisplayString(IShortcutKeymap keymap)
    {
        var text = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) { text.Add("Ctrl"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) { text.Add("Alt"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) { text.Add("Shift"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) { text.Add(keymap.SuperLabel); }
        text.Add(keymap.LabelFor(KeyCode));
        return string.Join('+', text);
    }

    // MARK: - Storage

    /// <summary>
    /// Stored as <c>keyCode:modifiers</c>, which is enough to rebuild it and short enough to
    /// read in a settings dump. The same shape as the macOS defaults value; only the numbers
    /// inside are this platform's.
    /// </summary>
    public string RawValue =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{KeyCode}:{(int)Modifiers}");

    public static KeyCombination? Parse(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue)) { return null; }
        string[] parts = rawValue.Split(':');
        if (parts.Length != 2) { return null; }
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int keyCode)) { return null; }
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int modifiers)) { return null; }
        return new KeyCombination(keyCode, (HotkeyModifiers)modifiers);
    }
}

/// <summary>
/// What a recorder does with a key press. Pure, so the rules are testable without a keyboard.
/// </summary>
public static class ShortcutRecording
{
    public abstract record Outcome
    {
        private Outcome()
        {
        }

        /// <summary>Recorded; stop listening.</summary>
        public sealed record Commit(KeyCombination Combination) : Outcome;

        /// <summary>Binding removed; stop listening.</summary>
        public sealed record Clear : Outcome;

        /// <summary>Stop listening, change nothing.</summary>
        public sealed record Cancel : Outcome;

        /// <summary>Not a shortcut anyone can use - keep listening and say why.</summary>
        public sealed record Reject(string Reason) : Outcome;
    }

    /// <summary>
    /// Escape leaves the field alone; Delete empties it. Both are what a person expects from
    /// every other recorder on the machine, and neither can be recorded as a shortcut because
    /// of it.
    /// </summary>
    public static Outcome Decide(int keyCode, HotkeyModifiers modifiers, IShortcutKeymap keymap)
    {
        var combination = new KeyCombination(keyCode, modifiers);
        bool bare = combination.Modifiers == HotkeyModifiers.None;
        switch (keymap.Special(keyCode))
        {
            case ShortcutSpecialKey.Escape when bare:
                return new Outcome.Cancel();
            case ShortcutSpecialKey.Delete when bare:
                return new Outcome.Clear();
        }

        if (!combination.IsValid(keymap))
        {
            return new Outcome.Reject(
                $"Add Ctrl, Alt or {keymap.SuperLabel} — a plain key would fire while you type.");
        }

        return new Outcome.Commit(combination);
    }
}

/// <summary>The keys a recorder treats specially when pressed on their own.</summary>
public enum ShortcutSpecialKey
{
    None,

    /// <summary>Cancels the recording.</summary>
    Escape,

    /// <summary>Backspace or Delete: empties the binding.</summary>
    Delete,
}

/// <summary>
/// What this platform knows about a shortcut's key that <see cref="KeyCombination"/> must not
/// assume: which numbers are the function row, which are Escape and Delete, and what is
/// printed on the key.
/// </summary>
/// <remarks>
/// The same shape as <see cref="IHotkeyKeymap"/>, for the same reason: the tables are data and
/// live in this assembly so both platforms' are checked by one test run. A platform project may
/// wrap the table to ask the live keyboard layout for <see cref="LabelFor"/> - which is what the
/// macOS original does through <c>UCKeyTranslate</c> - and fall back to it for everything else.
/// </remarks>
public interface IShortcutKeymap
{
    /// <summary>Whether the code is one of F1…F24, which type nothing on their own.</summary>
    bool IsFunctionKey(int keyCode);

    ShortcutSpecialKey Special(int keyCode);

    /// <summary>
    /// What is printed on the key: <c>C</c>, <c>F13</c>, <c>Space</c>. Never empty - an unknown
    /// code reads as <c>Key 123</c> rather than as nothing, so a stored shortcut from another
    /// keyboard still has a name.
    /// </summary>
    string LabelFor(int keyCode);

    /// <summary>The name of the key between Ctrl and Alt: <c>Win</c> here, <c>Super</c> there.</summary>
    string SuperLabel { get; }
}
