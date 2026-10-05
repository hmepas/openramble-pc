using Avalonia.Input;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// From the key Avalonia reports to the number the kernel reports for the same key.
/// </summary>
/// <remarks>
/// <para>
/// The shortcut recorder lives in an Avalonia window and hears keys as Avalonia names them; the
/// shortcut monitor lives on <c>/dev/input</c> and hears them as evdev keycodes from
/// <c>linux/input-event-codes.h</c>. A chord recorded in one vocabulary and matched in the
/// other needs a translation, and this is it - a table, because both sides are tables.
/// </para>
/// <para>
/// <see cref="PhysicalKey"/> rather than <see cref="Key"/>, on purpose. The physical key names a
/// position on the keyboard (Avalonia derives it from the X11/Wayland scancode, which is the
/// evdev code plus eight), where <see cref="Key"/> names what the key currently types - and a
/// shortcut lives on the physical key, not on the letter. That is the same rule the macOS
/// original follows when it asks the layout for a label rather than remembering the character:
/// a chord recorded on a Latin layout must still be the same key after switching to Cyrillic.
/// </para>
/// <para>
/// Keys with no entry map to <c>null</c> and cannot be recorded. That is the honest answer:
/// a key the monitor could never see (media keys on some devices, keys the compositor keeps)
/// must not be stored as a shortcut that then never fires.
/// </para>
/// </remarks>
internal static class EvdevKeyCodes
{
    /// <summary>The evdev keycode of a physical key, or <c>null</c> when there is none to give.</summary>
    internal static int? Of(PhysicalKey key) => key switch
    {
        PhysicalKey.Escape => 1,
        PhysicalKey.Digit1 => 2,
        PhysicalKey.Digit2 => 3,
        PhysicalKey.Digit3 => 4,
        PhysicalKey.Digit4 => 5,
        PhysicalKey.Digit5 => 6,
        PhysicalKey.Digit6 => 7,
        PhysicalKey.Digit7 => 8,
        PhysicalKey.Digit8 => 9,
        PhysicalKey.Digit9 => 10,
        PhysicalKey.Digit0 => 11,
        PhysicalKey.Minus => 12,
        PhysicalKey.Equal => 13,
        PhysicalKey.Backspace => 14,
        PhysicalKey.Tab => 15,
        PhysicalKey.Q => 16,
        PhysicalKey.W => 17,
        PhysicalKey.E => 18,
        PhysicalKey.R => 19,
        PhysicalKey.T => 20,
        PhysicalKey.Y => 21,
        PhysicalKey.U => 22,
        PhysicalKey.I => 23,
        PhysicalKey.O => 24,
        PhysicalKey.P => 25,
        PhysicalKey.BracketLeft => 26,
        PhysicalKey.BracketRight => 27,
        PhysicalKey.Enter => 28,
        PhysicalKey.A => 30,
        PhysicalKey.S => 31,
        PhysicalKey.D => 32,
        PhysicalKey.F => 33,
        PhysicalKey.G => 34,
        PhysicalKey.H => 35,
        PhysicalKey.J => 36,
        PhysicalKey.K => 37,
        PhysicalKey.L => 38,
        PhysicalKey.Semicolon => 39,
        PhysicalKey.Quote => 40,
        PhysicalKey.Backquote => 41,
        PhysicalKey.Backslash => 43,
        PhysicalKey.Z => 44,
        PhysicalKey.X => 45,
        PhysicalKey.C => 46,
        PhysicalKey.V => 47,
        PhysicalKey.B => 48,
        PhysicalKey.N => 49,
        PhysicalKey.M => 50,
        PhysicalKey.Comma => 51,
        PhysicalKey.Period => 52,
        PhysicalKey.Slash => 53,
        PhysicalKey.NumPadMultiply => 55,
        PhysicalKey.Space => 57,
        PhysicalKey.F1 => 59,
        PhysicalKey.F2 => 60,
        PhysicalKey.F3 => 61,
        PhysicalKey.F4 => 62,
        PhysicalKey.F5 => 63,
        PhysicalKey.F6 => 64,
        PhysicalKey.F7 => 65,
        PhysicalKey.F8 => 66,
        PhysicalKey.F9 => 67,
        PhysicalKey.F10 => 68,
        PhysicalKey.NumPad7 => 71,
        PhysicalKey.NumPad8 => 72,
        PhysicalKey.NumPad9 => 73,
        PhysicalKey.NumPadSubtract => 74,
        PhysicalKey.NumPad4 => 75,
        PhysicalKey.NumPad5 => 76,
        PhysicalKey.NumPad6 => 77,
        PhysicalKey.NumPadAdd => 78,
        PhysicalKey.NumPad1 => 79,
        PhysicalKey.NumPad2 => 80,
        PhysicalKey.NumPad3 => 81,
        PhysicalKey.NumPad0 => 82,
        PhysicalKey.NumPadDecimal => 83,
        PhysicalKey.IntlBackslash => 86,
        PhysicalKey.F11 => 87,
        PhysicalKey.F12 => 88,
        PhysicalKey.NumPadEnter => 96,
        PhysicalKey.NumPadDivide => 98,
        PhysicalKey.PrintScreen => 99,
        PhysicalKey.Home => 102,
        PhysicalKey.ArrowUp => 103,
        PhysicalKey.PageUp => 104,
        PhysicalKey.ArrowLeft => 105,
        PhysicalKey.ArrowRight => 106,
        PhysicalKey.End => 107,
        PhysicalKey.ArrowDown => 108,
        PhysicalKey.PageDown => 109,
        PhysicalKey.Insert => 110,
        PhysicalKey.Delete => 111,
        PhysicalKey.Pause => 119,
        PhysicalKey.F13 => 183,
        PhysicalKey.F14 => 184,
        PhysicalKey.F15 => 185,
        PhysicalKey.F16 => 186,
        PhysicalKey.F17 => 187,
        PhysicalKey.F18 => 188,
        PhysicalKey.F19 => 189,
        PhysicalKey.F20 => 190,
        PhysicalKey.F21 => 191,
        PhysicalKey.F22 => 192,
        PhysicalKey.F23 => 193,
        PhysicalKey.F24 => 194,
        _ => null,
    };

    /// <summary>
    /// Whether the key is itself a modifier or a lock key.
    /// </summary>
    /// <remarks>
    /// A recorder ignores these: the person is half-way through a chord, not pressing a key.
    /// The macOS original never sees them at all - AppKit reports a modifier going down as a
    /// flags change, not a key down - and the same silence is reproduced here by hand.
    /// </remarks>
    internal static bool IsModifier(PhysicalKey key) => key is
        PhysicalKey.ControlLeft or PhysicalKey.ControlRight
        or PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight
        or PhysicalKey.AltLeft or PhysicalKey.AltRight
        or PhysicalKey.MetaLeft or PhysicalKey.MetaRight
        or PhysicalKey.CapsLock or PhysicalKey.NumLock or PhysicalKey.ScrollLock;

    /// <summary>
    /// The modifiers Avalonia reports, as the chord machinery counts them. Sides are already
    /// merged on both sides; <c>Meta</c> is the key evdev calls Meta and the label calls Super.
    /// </summary>
    internal static HotkeyModifiers Modifiers(KeyModifiers modifiers)
    {
        HotkeyModifiers held = HotkeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) { held |= HotkeyModifiers.Control; }
        if (modifiers.HasFlag(KeyModifiers.Alt)) { held |= HotkeyModifiers.Alt; }
        if (modifiers.HasFlag(KeyModifiers.Shift)) { held |= HotkeyModifiers.Shift; }
        if (modifiers.HasFlag(KeyModifiers.Meta)) { held |= HotkeyModifiers.Windows; }
        return held;
    }
}
