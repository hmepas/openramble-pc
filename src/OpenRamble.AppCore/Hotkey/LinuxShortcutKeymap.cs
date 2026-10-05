using System.Globalization;

namespace OpenRamble.AppCore;

/// <summary>
/// Shortcut keys as the kernel reports them: evdev keycodes from <c>linux/input-event-codes.h</c>.
/// </summary>
/// <remarks>
/// The same convention as <see cref="LinuxHotkeyKeymap"/>: these are kernel codes, not X11's
/// (which adds 8). Letter and digit labels follow the US layout the kernel's key names are
/// written in - <c>KEY_Q</c> is the key left of W on every keyboard, whatever it types. A
/// platform wrapper may ask XKB for the live label; the table needs no display server.
/// </remarks>
public sealed class LinuxShortcutKeymap : IShortcutKeymap
{
    private const int KeyEsc = 1;
    private const int KeyBackspace = 14;
    private const int KeyTab = 15;
    private const int KeyEnter = 28;
    private const int KeySpace = 57;
    private const int KeyF1 = 59;
    private const int KeyF10 = 68;
    private const int KeyF11 = 87;
    private const int KeyF12 = 88;
    private const int KeyHome = 102;
    private const int KeyUp = 103;
    private const int KeyPageUp = 104;
    private const int KeyLeft = 105;
    private const int KeyRight = 106;
    private const int KeyEnd = 107;
    private const int KeyDown = 108;
    private const int KeyPageDown = 109;
    private const int KeyInsert = 110;
    private const int KeyDelete = 111;
    private const int KeyF13 = 183;
    private const int KeyF24 = 194;

    /// <summary>The top row: <c>KEY_1</c> … <c>KEY_9</c>, <c>KEY_0</c>.</summary>
    private const string DigitRow = "1234567890";

    /// <summary>The three letter rows, in keycode order from <c>KEY_Q</c> (16), <c>KEY_A</c> (30), <c>KEY_Z</c> (44).</summary>
    private const string RowOne = "QWERTYUIOP";
    private const string RowTwo = "ASDFGHJKL";
    private const string RowThree = "ZXCVBNM";

    private LinuxShortcutKeymap()
    {
    }

    public static LinuxShortcutKeymap Instance { get; } = new();

    public string SuperLabel => "Super";

    /// <inheritdoc/>
    public bool IsFunctionKey(int keyCode) =>
        keyCode is (>= KeyF1 and <= KeyF10) or KeyF11 or KeyF12 or (>= KeyF13 and <= KeyF24);

    /// <inheritdoc/>
    public ShortcutSpecialKey Special(int keyCode) => keyCode switch
    {
        KeyEsc => ShortcutSpecialKey.Escape,
        KeyBackspace or KeyDelete => ShortcutSpecialKey.Delete,
        _ => ShortcutSpecialKey.None,
    };

    /// <inheritdoc/>
    public string LabelFor(int keyCode)
    {
        int? functionNumber = keyCode switch
        {
            >= KeyF1 and <= KeyF10 => keyCode - KeyF1 + 1,
            KeyF11 => 11,
            KeyF12 => 12,
            >= KeyF13 and <= KeyF24 => keyCode - KeyF13 + 13,
            _ => null,
        };
        if (functionNumber is { } number)
        {
            return "F" + number.ToString(CultureInfo.InvariantCulture);
        }

        if (keyCode is >= 2 and <= 11) { return DigitRow[keyCode - 2].ToString(); }
        if (keyCode is >= 16 and <= 25) { return RowOne[keyCode - 16].ToString(); }
        if (keyCode is >= 30 and <= 38) { return RowTwo[keyCode - 30].ToString(); }
        if (keyCode is >= 44 and <= 50) { return RowThree[keyCode - 44].ToString(); }

        return keyCode switch
        {
            KeyEsc => "Esc",
            KeyBackspace => "Backspace",
            KeyTab => "Tab",
            KeyEnter => "Enter",
            KeySpace => "Space",
            KeyHome => "Home",
            KeyUp => "Up",
            KeyPageUp => "PageUp",
            KeyLeft => "Left",
            KeyRight => "Right",
            KeyEnd => "End",
            KeyDown => "Down",
            KeyPageDown => "PageDown",
            KeyInsert => "Insert",
            KeyDelete => "Delete",
            _ => "Key " + keyCode.ToString(CultureInfo.InvariantCulture),
        };
    }
}
