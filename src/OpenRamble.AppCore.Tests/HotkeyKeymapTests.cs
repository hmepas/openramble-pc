using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The two numberings the hotkey wears, checked side by side.
///
/// A keymap is a table of magic numbers, and a wrong entry in one is not a crash: it is a
/// dictation key that answers to some other physical key, on somebody else's machine, months
/// later. There is nothing to reason about here and everything to look up, so every number below
/// is written out against the header it came from - <c>winuser.h</c> for the virtual-key codes,
/// <c>linux/input-event-codes.h</c> for the evdev ones - rather than derived from the code under
/// test.
/// </summary>
/// <remarks>
/// Both tables live in the neutral assembly precisely so that both are checked by this run: the
/// Linux host that builds this repository can compile a virtual-key code perfectly well, and a
/// Windows table verified only on Windows would be verified only by whoever last had a Windows
/// machine to hand.
/// </remarks>
public sealed class HotkeyKeymapTests
{
    /// <summary>Virtual-key codes, <c>winuser.h</c>.</summary>
    private const int VkCapital = 0x14;
    private const int VkF13 = 0x7C;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;
    private const int VkLetterC = 0x43;
    private const int VkF12 = 0x7B;

    /// <summary>Evdev keycodes, <c>linux/input-event-codes.h</c>.</summary>
    private const int KeyLeftCtrl = 29;
    private const int KeyLeftShift = 42;
    private const int KeyRightShift = 54;
    private const int KeyLeftAlt = 56;
    private const int KeyCapsLock = 58;
    private const int KeyRightCtrl = 97;
    private const int KeyRightAlt = 100;
    private const int KeyLeftMeta = 125;
    private const int KeyRightMeta = 126;
    private const int KeyF13 = 183;
    private const int KeyC = 46;
    private const int KeyF12 = 88;

    // MARK: - Windows

    [Fact]
    public void TheWindowsTableIsTheVirtualKeyCodes()
    {
        IHotkeyKeymap keymap = WindowsHotkeyKeymap.Instance;

        Assert.Equal(VkRightControl, keymap.CodeFor(DictationHotkey.RightCtrl));
        Assert.Equal(VkRightShift, keymap.CodeFor(DictationHotkey.RightShift));
        Assert.Equal(VkRightMenu, keymap.CodeFor(DictationHotkey.RightAlt));
        Assert.Equal(VkCapital, keymap.CodeFor(DictationHotkey.CapsLock));
        Assert.Equal(VkF13, keymap.CodeFor(DictationHotkey.F13));

        // Never the LEFT twin of a modifier: the left keys carry the shortcuts people type.
        int[] offered = DictationHotkeyInfo.AllCases.Select(keymap.CodeFor).ToArray();
        Assert.DoesNotContain(VkLeftControl, offered);
        Assert.DoesNotContain(VkLeftMenu, offered);
        Assert.DoesNotContain(VkLeftShift, offered);
    }

    /// <summary>
    /// The combined codes count too.
    ///
    /// Windows is the platform with a <c>VK_SHIFT</c> as well as a <c>VK_LSHIFT</c> and a
    /// <c>VK_RSHIFT</c>, and a hook that reports the combined one during a hold would otherwise
    /// read as a foreign key and abort the dictation as a shortcut.
    /// </summary>
    [Fact]
    public void TheWindowsModifiersCoverBothSidesAndTheCombinedCodes()
    {
        IHotkeyKeymap keymap = WindowsHotkeyKeymap.Instance;

        int[] modifiers =
        [
            VkShift, VkLeftShift, VkRightShift,
            VkControl, VkLeftControl, VkRightControl,
            VkMenu, VkLeftMenu, VkRightMenu,
            VkLeftWindows, VkRightWindows,
        ];
        Assert.All(modifiers, code => Assert.True(keymap.IsModifierCode(code), $"0x{code:X2}"));

        // Caps Lock and F13 are hotkey candidates and are NOT modifiers: holding either raises no
        // chord, so anything held alongside them is a shortcut in progress.
        Assert.False(keymap.IsModifierCode(VkCapital));
        Assert.False(keymap.IsModifierCode(VkF13));
        Assert.False(keymap.IsModifierCode(VkLetterC));
        Assert.False(keymap.IsModifierCode(VkF12));
    }

    // MARK: - Linux

    [Fact]
    public void TheLinuxTableIsTheEvdevKeycodes()
    {
        IHotkeyKeymap keymap = LinuxHotkeyKeymap.Instance;

        Assert.Equal(KeyRightCtrl, keymap.CodeFor(DictationHotkey.RightCtrl));
        Assert.Equal(KeyRightShift, keymap.CodeFor(DictationHotkey.RightShift));
        Assert.Equal(KeyRightAlt, keymap.CodeFor(DictationHotkey.RightAlt));
        Assert.Equal(KeyCapsLock, keymap.CodeFor(DictationHotkey.CapsLock));
        Assert.Equal(KeyF13, keymap.CodeFor(DictationHotkey.F13));

        // Never the LEFT twin of a modifier: the left keys carry the shortcuts people type.
        int[] offered = DictationHotkeyInfo.AllCases.Select(keymap.CodeFor).ToArray();
        Assert.DoesNotContain(KeyLeftCtrl, offered);
        Assert.DoesNotContain(KeyLeftAlt, offered);
        Assert.DoesNotContain(KeyLeftShift, offered);
    }

    /// <summary>
    /// Evdev has no combined codes at all - the side IS the key, which is what the gesture machine
    /// wanted from the start.
    /// </summary>
    [Fact]
    public void TheLinuxModifiersAreTheEightSidedKeys()
    {
        IHotkeyKeymap keymap = LinuxHotkeyKeymap.Instance;

        int[] modifiers =
        [
            KeyLeftShift, KeyRightShift,
            KeyLeftCtrl, KeyRightCtrl,
            KeyLeftAlt, KeyRightAlt,
            KeyLeftMeta, KeyRightMeta,
        ];
        Assert.All(modifiers, code => Assert.True(keymap.IsModifierCode(code), code.ToString()));

        Assert.False(keymap.IsModifierCode(KeyCapsLock));
        Assert.False(keymap.IsModifierCode(KeyF13));
        Assert.False(keymap.IsModifierCode(KeyC));
        Assert.False(keymap.IsModifierCode(KeyF12));
    }

    /// <summary>
    /// The evdev numbers are the kernel's, not the X server's.
    ///
    /// X11 adds 8 to every kernel keycode, and the two ranges overlap: <c>KEY_RIGHTCTRL</c> is 97
    /// to the kernel and 105 to X, while 97 to X is <c>KEY_KP1</c>. An event source that forgot
    /// the offset would therefore not fail loudly - it would arm the numeric keypad. The check
    /// that catches that is this one: the table must hold the kernel value.
    /// </summary>
    [Fact]
    public void TheLinuxTableIsKernelCodesAndNotX11Codes()
    {
        IHotkeyKeymap keymap = LinuxHotkeyKeymap.Instance;

        // The four spelled out as X would report them, so that a table silently shifted by 8 is
        // a failure here and not a surprise in the field.
        Assert.NotEqual(KeyRightCtrl + 8, keymap.CodeFor(DictationHotkey.RightCtrl));
        Assert.NotEqual(KeyRightShift + 8, keymap.CodeFor(DictationHotkey.RightShift));
        Assert.NotEqual(KeyRightAlt + 8, keymap.CodeFor(DictationHotkey.RightAlt));
        Assert.NotEqual(KeyCapsLock + 8, keymap.CodeFor(DictationHotkey.CapsLock));
    }

    // MARK: - What both tables must promise

    /// <summary>
    /// Whatever the numbers are, no two candidates may share one.
    ///
    /// Two candidates sharing a code is one key wearing two names in the picker, and choosing
    /// either would arm the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keymaps))]
    public void EveryCandidateHasItsOwnCode(IHotkeyKeymap keymap)
    {
        int[] codes = DictationHotkeyInfo.AllCases.Select(keymap.CodeFor).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    /// <summary>A hotkey that is a chord modifier must be recognised as one by its own table.</summary>
    [Theory]
    [MemberData(nameof(Keymaps))]
    public void AModifierHotkeyIsAModifierCode(IHotkeyKeymap keymap)
    {
        foreach (DictationHotkey hotkey in DictationHotkeyInfo.AllCases)
        {
            Assert.Equal(
                hotkey.Kind != HotkeyModifiers.None,
                keymap.IsModifierCode(keymap.CodeFor(hotkey)));
        }
    }

    /// <summary>An enum value from nowhere is not a key, and inventing a code for it would hide it.</summary>
    [Theory]
    [MemberData(nameof(Keymaps))]
    public void AnUnknownHotkeyHasNoCode(IHotkeyKeymap keymap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => keymap.CodeFor((DictationHotkey)999));
    }

    /// <summary>
    /// The same gesture, told in either numbering, is the same gesture.
    ///
    /// This is the whole claim the keymap split makes: the machine holds the rules, the table
    /// holds the numbers, and swapping the table changes nothing a person would notice.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keymaps))]
    public void TheGestureMachineReadsEitherNumbering(IHotkeyKeymap keymap)
    {
        var start = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl, keymap);
        int hotkeyCode = keymap.CodeFor(DictationHotkey.RightCtrl);
        int leftTwin = keymap is LinuxHotkeyKeymap ? KeyLeftCtrl : VkLeftControl;

        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(new HotkeyEvent(hotkeyCode, IsDown: true, HotkeyModifiers.Control, start)));

        // The left twin mid-hold is still just Ctrl, in both numberings.
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(new HotkeyEvent(
                leftTwin,
                IsDown: true,
                HotkeyModifiers.Control,
                start.AddMilliseconds(200))));

        Assert.Equal(
            HotkeyAction.Release(TimeSpan.Zero),
            machine.Handle(new HotkeyEvent(
                hotkeyCode,
                IsDown: false,
                HotkeyModifiers.Control,
                start.AddMilliseconds(400))));
    }

    public static TheoryData<IHotkeyKeymap> Keymaps() =>
        [WindowsHotkeyKeymap.Instance, LinuxHotkeyKeymap.Instance];
}
