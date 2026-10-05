using System.Globalization;

namespace OpenRamble.AppCore;

/// <summary>
/// Shortcut keys as <c>WH_KEYBOARD_LL</c> reports them: virtual-key codes from <c>winuser.h</c>.
/// </summary>
/// <remarks>
/// Data, not an adapter, like <see cref="WindowsHotkeyKeymap"/>. The letter and digit labels
/// are the US layout's: a virtual-key code names a physical key, and Windows itself shows
/// <c>Ctrl+C</c> in menus whatever layout is typing, so the label is the key's own character
/// rather than the layout's. A platform wrapper may refine this through <c>MapVirtualKey</c>
/// and <c>ToUnicode</c>; the table is the answer that needs no <c>user32</c>.
/// </remarks>
public sealed class WindowsShortcutKeymap : IShortcutKeymap
{
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkPrior = 0x21;
    private const int VkNext = 0x22;
    private const int VkEnd = 0x23;
    private const int VkHome = 0x24;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkInsert = 0x2D;
    private const int VkDelete = 0x2E;
    private const int VkF1 = 0x70;
    private const int VkF24 = 0x87;

    private WindowsShortcutKeymap()
    {
    }

    public static WindowsShortcutKeymap Instance { get; } = new();

    public string SuperLabel => "Win";

    /// <inheritdoc/>
    public bool IsFunctionKey(int keyCode) => keyCode is >= VkF1 and <= VkF24;

    /// <inheritdoc/>
    public ShortcutSpecialKey Special(int keyCode) => keyCode switch
    {
        VkEscape => ShortcutSpecialKey.Escape,
        VkBack or VkDelete => ShortcutSpecialKey.Delete,
        _ => ShortcutSpecialKey.None,
    };

    /// <inheritdoc/>
    public string LabelFor(int keyCode)
    {
        if (IsFunctionKey(keyCode))
        {
            return "F" + (keyCode - VkF1 + 1).ToString(CultureInfo.InvariantCulture);
        }

        // Digits and letters share their ASCII codes with the virtual keys.
        if (keyCode is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)keyCode).ToString();
        }

        return keyCode switch
        {
            VkBack => "Backspace",
            VkTab => "Tab",
            VkReturn => "Enter",
            VkEscape => "Esc",
            VkSpace => "Space",
            VkPrior => "PageUp",
            VkNext => "PageDown",
            VkEnd => "End",
            VkHome => "Home",
            VkLeft => "Left",
            VkUp => "Up",
            VkRight => "Right",
            VkDown => "Down",
            VkInsert => "Insert",
            VkDelete => "Delete",
            _ => "Key " + keyCode.ToString(CultureInfo.InvariantCulture),
        };
    }
}
