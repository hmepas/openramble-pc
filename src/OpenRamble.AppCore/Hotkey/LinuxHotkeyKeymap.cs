namespace OpenRamble.AppCore;

/// <summary>
/// The hotkey candidates as the kernel reports them: evdev keycodes from
/// <c>linux/input-event-codes.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evdev, not X11 and not XKB.</b> The kernel's <c>EV_KEY</c> codes are what <c>/dev/input</c>
/// and libinput deliver, and they are what this table holds. The X server historically adds 8 to
/// every one of them, so an X11-based event source has to subtract that 8 before it asks anything
/// here - a translation that belongs to whoever reads X11, not to the table. Getting it wrong is
/// not a crash: it is a hotkey that silently answers to the wrong physical key, which is why the
/// convention is written down rather than assumed.
/// </para>
/// <para>
/// <b>No combined codes.</b> Windows has <c>VK_SHIFT</c> alongside <c>VK_LSHIFT</c> and
/// <c>VK_RSHIFT</c>; evdev has only the two sides, which is what the gesture machine wanted all
/// along - the side IS the code, with nothing to mask off.
/// </para>
/// </remarks>
public sealed class LinuxHotkeyKeymap : IHotkeyKeymap
{
    /// <summary>Evdev keycodes, from <c>linux/input-event-codes.h</c>.</summary>
    private const int KeyCapsLock = 58;
    private const int KeyRightShift = 54;
    private const int KeyRightCtrl = 97;
    private const int KeyRightAlt = 100;
    private const int KeyLeftCtrl = 29;
    private const int KeyLeftShift = 42;
    private const int KeyF13 = 183;

    private LinuxHotkeyKeymap()
    {
    }

    /// <summary>The table is immutable and stateless; one of it is enough for the process.</summary>
    public static LinuxHotkeyKeymap Instance { get; } = new();

    /// <inheritdoc/>
    public int CodeFor(DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightCtrl => KeyRightCtrl,
        DictationHotkey.RightShift => KeyRightShift,
        DictationHotkey.RightAlt => KeyRightAlt,
        DictationHotkey.CapsLock => KeyCapsLock,
        DictationHotkey.LeftCtrl => KeyLeftCtrl,
        DictationHotkey.LeftShift => KeyLeftShift,
        // Not offered by the picker, and no keyboard sends it unaided - but a settings file
        // written by an earlier build may still say F13, and a table with a hole in it would
        // throw where the rest of the code merely falls back to the default.
        DictationHotkey.F13 => KeyF13,
        _ => throw new ArgumentOutOfRangeException(nameof(hotkey)),
    };

    /// <inheritdoc/>
    public bool IsModifierCode(int keyCode) => keyCode switch
    {
        42 or 54 => true,   // KEY_LEFTSHIFT, KEY_RIGHTSHIFT
        29 or 97 => true,   // KEY_LEFTCTRL, KEY_RIGHTCTRL
        56 or 100 => true,  // KEY_LEFTALT, KEY_RIGHTALT
        125 or 126 => true, // KEY_LEFTMETA, KEY_RIGHTMETA
        _ => false,
    };
}
