namespace OpenRamble.AppCore;

/// <summary>
/// The hotkey candidates as <c>WH_KEYBOARD_LL</c> reports them: virtual-key codes from
/// <c>winuser.h</c>.
/// </summary>
/// <remarks>
/// Data, not an adapter: no Win32 call is made here and none is needed, so the table is compiled
/// and tested on any host. <c>KeyboardHook</c> is what turns these numbers into a hook, and it is
/// the only place that needs Windows.
/// </remarks>
public sealed class WindowsHotkeyKeymap : IHotkeyKeymap
{
    /// <summary>Virtual-key codes, from <c>winuser.h</c>.</summary>
    private const int VkRightControl = 0xA3;
    private const int VkRightShift = 0xA1;
    private const int VkRightMenu = 0xA5;
    private const int VkCapital = 0x14;
    private const int VkF13 = 0x7C;

    private WindowsHotkeyKeymap()
    {
    }

    /// <summary>The table is immutable and stateless; one of it is enough for the process.</summary>
    public static WindowsHotkeyKeymap Instance { get; } = new();

    /// <inheritdoc/>
    public int CodeFor(DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightCtrl => VkRightControl,
        DictationHotkey.RightShift => VkRightShift,
        DictationHotkey.RightAlt => VkRightMenu,
        DictationHotkey.CapsLock => VkCapital,
        DictationHotkey.F13 => VkF13,
        _ => throw new ArgumentOutOfRangeException(nameof(hotkey)),
    };

    /// <inheritdoc/>
    public bool IsModifierCode(int keyCode) => keyCode switch
    {
        0x10 or 0xA0 or 0xA1 => true, // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        0x11 or 0xA2 or 0xA3 => true, // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 => true, // VK_MENU, VK_LMENU, VK_RMENU
        0x5B or 0x5C => true,         // VK_LWIN, VK_RWIN
        _ => false,
    };
}
