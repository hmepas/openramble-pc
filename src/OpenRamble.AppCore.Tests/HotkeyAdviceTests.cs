using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Ported from <c>FnKeyUsageTests</c>. Fn does not exist on Windows, so the subject is recast:
/// the question "what else already uses this key" is the same, the answers are the ones a PC
/// keyboard can actually give.
/// </summary>
public sealed class HotkeyAdviceTests
{
    [Fact]
    public void Scenario001()
    {
        Assert.Equal(WindowsKeyUsage.Free, WindowsKeyUsageInfo.Of(DictationHotkey.RightCtrl, layoutHasAltGraph: true));
        Assert.Equal(WindowsKeyUsage.AltGraph, WindowsKeyUsageInfo.Of(DictationHotkey.RightAlt, layoutHasAltGraph: true));
        Assert.Equal(WindowsKeyUsage.Free, WindowsKeyUsageInfo.Of(DictationHotkey.RightAlt, layoutHasAltGraph: false));
        Assert.Equal(WindowsKeyUsage.CapsLockToggle, WindowsKeyUsageInfo.Of(DictationHotkey.CapsLock, layoutHasAltGraph: false));
        Assert.Equal(
            WindowsKeyUsage.MissingOnManyKeyboards,
            WindowsKeyUsageInfo.Of(DictationHotkey.F13, layoutHasAltGraph: false));
    }

    /// <summary>
    /// Staying silent here is more expensive than warning one extra time: the system could have
    /// claimed a new action, and the person would think we are broken.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        Assert.True(WindowsKeyUsage.Unknown.IsTakenBySystem);
        Assert.True(WindowsKeyUsage.CapsLockToggle.IsTakenBySystem);
        Assert.True(WindowsKeyUsage.AltGraph.IsTakenBySystem);
        Assert.False(WindowsKeyUsage.Free.IsTakenBySystem);
        // Not taken - absent. It earns a warning for a different reason.
        Assert.False(WindowsKeyUsage.MissingOnManyKeyboards.IsTakenBySystem);
    }

    /// <summary>The default choice never warns: only a person who picked otherwise gets here.</summary>
    [Fact]
    public void Scenario003()
    {
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightCtrl, WindowsKeyUsage.Free));
        Assert.Null(HotkeyAdvice.Warning(DictationHotkey.RightAlt, WindowsKeyUsage.Free));
    }

    /// <summary>A key the system already uses: dictation and that action fire together.</summary>
    [Fact]
    public void Scenario004()
    {
        string caps = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.CapsLock, WindowsKeyUsage.CapsLockToggle));
        Assert.Contains("the caps lock toggle", caps, StringComparison.Ordinal);

        string altGraph = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.RightAlt, WindowsKeyUsage.AltGraph));
        Assert.Contains("AltGr character composition", altGraph, StringComparison.Ordinal);
    }

    /// <summary>An unclassified key still says where to look.</summary>
    [Fact]
    public void Scenario005()
    {
        string warning = Assert.IsType<string>(
            HotkeyAdvice.Warning(DictationHotkey.RightCtrl, WindowsKeyUsage.Unknown));
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
            HotkeyAdvice.Warning(DictationHotkey.F13, WindowsKeyUsage.MissingOnManyKeyboards));
        Assert.Contains("F13", warning, StringComparison.Ordinal);
    }
}
