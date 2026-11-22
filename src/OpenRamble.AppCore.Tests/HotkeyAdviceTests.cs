using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Ported from <c>FnKeyUsageTests</c>. Fn does not exist on a PC, so the subject is recast:
/// the question "what else already uses this key" is the same, the answers are the ones a PC
/// keyboard can actually give.
/// </summary>
public sealed class HotkeyAdviceTests
{
    /// <summary>
    /// The platform half of the copy. The classification above it is shared - Caps Lock shouts
    /// and AltGr composes on every desktop - so only this sentence is chosen per platform.
    /// </summary>
    private const string WindowsHint = HotkeyAdvice.WindowsSettingsHint;

    [Fact]
    public void Scenario001()
    {
        Assert.Equal(KeyUsage.Free, KeyUsageInfo.Of(DictationHotkey.RightCtrl, layoutHasAltGraph: true, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
        Assert.Equal(KeyUsage.AltGraph, KeyUsageInfo.Of(DictationHotkey.RightAlt, layoutHasAltGraph: true, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
        Assert.Equal(KeyUsage.Free, KeyUsageInfo.Of(DictationHotkey.RightAlt, layoutHasAltGraph: false, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
        Assert.Equal(KeyUsage.CapsLockToggle, KeyUsageInfo.Of(DictationHotkey.CapsLock, layoutHasAltGraph: false, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
        Assert.Equal(
            KeyUsage.MissingOnManyKeyboards,
            KeyUsageInfo.Of(DictationHotkey.F13, layoutHasAltGraph: false, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
    }

    /// <summary>
    /// Staying silent here is more expensive than warning one extra time: the system could have
    /// claimed a new action, and the person would think we are broken.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        Assert.True(KeyUsage.Unknown.IsTakenBySystem);
        Assert.True(KeyUsage.CapsLockToggle.IsTakenBySystem);
        Assert.True(KeyUsage.AltGraph.IsTakenBySystem);
        Assert.False(KeyUsage.Free.IsTakenBySystem);
        // Not taken - absent. It earns a warning for a different reason.
        Assert.False(KeyUsage.MissingOnManyKeyboards.IsTakenBySystem);
    }

    /// <summary>The default choice never warns: only a person who picked otherwise gets here.</summary>
    [Fact]
    public void Scenario003()
    {
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightCtrl, KeyUsage.Free, WindowsHint));
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightAlt, KeyUsage.Free, WindowsHint));
    }

    /// <summary>A key the system already uses: dictation and that action fire together.</summary>
    [Fact]
    public void Scenario004()
    {
        string caps = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.CapsLock, KeyUsage.CapsLockToggle, WindowsHint));
        Assert.Contains("the caps lock toggle", caps, StringComparison.Ordinal);

        string altGraph = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.RightAlt, KeyUsage.AltGraph, WindowsHint));
        Assert.Contains("AltGr character composition", altGraph, StringComparison.Ordinal);
    }

    /// <summary>An unclassified key still says where to look.</summary>
    [Fact]
    public void Scenario005()
    {
        string warning = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.RightCtrl, KeyUsage.Unknown, WindowsHint));
        Assert.Contains("Right Ctrl", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Even a completely free key can warn.
    ///
    /// The macOS counterpart is the external keyboard without a 🌐 key - a common case on a Mac
    /// mini, and there dictation simply never starts with no way to tell why. F13 is the same
    /// trap on a PC: nothing takes it, and almost no keyboard sends it.
    /// </summary>
    [Fact]
    public void Scenario006()
    {
        string warning = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.F13, KeyUsage.MissingOnManyKeyboards, WindowsHint));
        Assert.Contains("F13", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Right Shift is a clean fallback, on every layout, with nothing to warn about.
    ///
    /// It is the answer to the failure mode Right Ctrl actually has on Windows - a virtual machine
    /// client or a remote desktop keeping one key for itself - so an advice line sending the
    /// person off to pick yet another key would be the opposite of help. Held alone it types
    /// nothing and toggles nothing, so there is genuinely nothing to say.
    /// </summary>
    [Fact]
    public void RightShiftIsFreeOnEveryLayoutAndNeverWarns()
    {
        Assert.Equal(
            KeyUsage.Free,
            KeyUsageInfo.Of(DictationHotkey.RightShift, layoutHasAltGraph: true, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
        Assert.Equal(
            KeyUsage.Free,
            KeyUsageInfo.Of(DictationHotkey.RightShift, layoutHasAltGraph: false, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightShift, KeyUsage.Free, WindowsHint));
    }

    /// <summary>
    /// Every offered key is classified, distinguishable and nameable.
    ///
    /// A key that fell through to <see cref="KeyUsage.Unknown"/> would warn about "a system
    /// action" nobody can name; two keys sharing a virtual-key code would be one key wearing two
    /// names in the picker, and picking either would arm the other.
    /// </summary>
    [Fact]
    public void EveryOfferedKeyIsClassifiedAndDistinct()
    {
        var codes = new HashSet<int>();
        var titles = new HashSet<string>(StringComparer.Ordinal);

        foreach (DictationHotkey hotkey in DictationHotkeyInfo.AllCases)
        {
            Assert.NotEqual(
                KeyUsage.Unknown,
                KeyUsageInfo.Of(hotkey, layoutHasAltGraph: false, sourceSwallowsKey: true, keydFoldsRightModifiers: false));
            Assert.True(
                codes.Add(WindowsHotkeyKeymap.Instance.CodeFor(hotkey)),
                $"{hotkey.Title} shares a key code");
            Assert.True(titles.Add(hotkey.Title), $"{hotkey.Title} is offered twice");
        }

        // The six the first-launch picker promises, in the order it promises them: the free
        // keys first, right side before left, and the two that carry a warning last.
        Assert.Equal(
            [
                DictationHotkey.RightCtrl,
                DictationHotkey.RightShift,
                DictationHotkey.LeftCtrl,
                DictationHotkey.LeftShift,
                DictationHotkey.RightAlt,
                DictationHotkey.CapsLock,
            ],
            DictationHotkeyInfo.AllCases);
    }

    // MARK: - The one sentence that is not shared

    /// <summary>
    /// The Windows warning is what it was before the platform split, to the character.
    ///
    /// The whole point of passing the sentence in is that nothing about the shipping copy moves;
    /// a refactor that quietly reworded a warning people have already read would be the failure
    /// this pins.
    /// </summary>
    [Fact]
    public void TheWindowsWarningIsUnchangedByTheSplit()
    {
        Assert.Equal(
            "Pressing Caps Lock already triggers the caps lock toggle — dictation and that "
            + "action will fire together unless OpenRamble is allowed to swallow the key. "
            + "To change this: Settings → Time & language → Typing.",
            HotkeyAdvice.Warning(DictationHotkey.CapsLock, KeyUsage.CapsLockToggle, WindowsHint));
    }

    /// <summary>
    /// Linux gets the same diagnosis and a different remedy - and no Windows Settings path.
    /// </summary>
    [Fact]
    public void TheLinuxWarningKeepsTheDiagnosisAndChangesTheRemedy()
    {
        string warning = Assert.IsType<string>(HotkeyAdvice.Warning(
            DictationHotkey.CapsLock,
            KeyUsage.CapsLockToggle,
            HotkeyAdvice.LinuxSettingsHint));

        Assert.Contains("the caps lock toggle", warning, StringComparison.Ordinal);
        Assert.EndsWith(HotkeyAdvice.LinuxSettingsHint, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Time & language", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// No hint means no dangling sentence: the warning still says what is wrong and stops there,
    /// rather than trailing off after "the key." with a space.
    /// </summary>
    [Fact]
    public void AWarningWithoutAHintEndsCleanly()
    {
        string warning = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.RightAlt, KeyUsage.AltGraph, settingsHint: null));

        Assert.Contains("AltGr character composition", warning, StringComparison.Ordinal);
        Assert.EndsWith("swallow the key.", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The F13 warning ignores the hint entirely: nothing has taken the key, so there is no
    /// setting to send anybody to - the problem is that the keyboard cannot send it.
    /// </summary>
    [Fact]
    public void TheHintOnlyReachesTheWarningThatNamesASystemAction()
    {
        Assert.Equal(
            HotkeyAdvice.Warning(DictationHotkey.F13, KeyUsage.MissingOnManyKeyboards, WindowsHint),
            HotkeyAdvice.Warning(DictationHotkey.F13, KeyUsage.MissingOnManyKeyboards, settingsHint: null));
    }

    // MARK: - What the machine does with the key

    /// <summary>
    /// keyd re-sends Right Ctrl and Right Shift as their left twins: the right key never arrives,
    /// and the warning says so and names the keys that do.
    /// </summary>
    [Theory]
    [InlineData(DictationHotkey.RightCtrl, "Left Ctrl")]
    [InlineData(DictationHotkey.RightShift, "Left Shift")]
    public void KeydFoldingARightModifierWarnsAndNamesTheLeftTwin(DictationHotkey hotkey, string twin)
    {
        KeyUsage usage = KeyUsageInfo.Of(
            hotkey, layoutHasAltGraph: false, sourceSwallowsKey: false, keydFoldsRightModifiers: true);
        Assert.Equal(KeyUsage.FoldedIntoLeftTwin, usage);

        string warning = Assert.IsType<string>(
            HotkeyAdvice.Warning(hotkey, usage, HotkeyAdvice.LinuxSettingsHint));
        Assert.Contains("keyd", warning, StringComparison.Ordinal);
        Assert.Contains($"re-sends {hotkey.Title} as {twin}", warning, StringComparison.Ordinal);
        Assert.Contains("Right Alt", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(HotkeyAdvice.LinuxSettingsHint, warning, StringComparison.Ordinal);
    }

    /// <summary>The keys keyd folds into are exactly the ones that still arrive.</summary>
    [Theory]
    [InlineData(DictationHotkey.LeftCtrl)]
    [InlineData(DictationHotkey.LeftShift)]
    public void KeydLeavesTheLeftKeysFree(DictationHotkey hotkey)
    {
        KeyUsage usage = KeyUsageInfo.Of(
            hotkey, layoutHasAltGraph: true, sourceSwallowsKey: false, keydFoldsRightModifiers: true);

        Assert.Equal(KeyUsage.Free, usage);
        Assert.Null(HotkeyAdvice.Warning(hotkey, usage, HotkeyAdvice.LinuxSettingsHint));
    }

    [Fact]
    public void WithoutKeydRightCtrlIsFree()
    {
        KeyUsage usage = KeyUsageInfo.Of(
            DictationHotkey.RightCtrl,
            layoutHasAltGraph: true,
            sourceSwallowsKey: false,
            keydFoldsRightModifiers: false);

        Assert.Equal(KeyUsage.Free, usage);
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightCtrl, usage, HotkeyAdvice.LinuxSettingsHint));
    }

    /// <summary>
    /// A source that reads alongside - evdev, or the Windows hook for anything but Caps Lock -
    /// leaves AltGr composing, and Right Alt plus a key is a chord that ends the hold. Nothing
    /// to warn about, even on a layout that has AltGr.
    /// </summary>
    [Fact]
    public void RightAltOnASourceThatDoesNotSwallowItIsFree()
    {
        KeyUsage usage = KeyUsageInfo.Of(
            DictationHotkey.RightAlt,
            layoutHasAltGraph: true,
            sourceSwallowsKey: false,
            keydFoldsRightModifiers: false);

        Assert.Equal(KeyUsage.Free, usage);
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightAlt, usage, HotkeyAdvice.LinuxSettingsHint));
    }

    /// <summary>A source that takes the key - a compositor keybind - does cost AltGr.</summary>
    [Fact]
    public void RightAltOnASwallowingSourceWithAnAltGraphLayoutWarns()
    {
        KeyUsage usage = KeyUsageInfo.Of(
            DictationHotkey.RightAlt,
            layoutHasAltGraph: true,
            sourceSwallowsKey: true,
            keydFoldsRightModifiers: false);

        Assert.Equal(KeyUsage.AltGraph, usage);
        string warning = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.RightAlt, usage, HotkeyAdvice.LinuxSettingsHint));
        Assert.Contains("AltGr character composition", warning, StringComparison.Ordinal);
    }
}
