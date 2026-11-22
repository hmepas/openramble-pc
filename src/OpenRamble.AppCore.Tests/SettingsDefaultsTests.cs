namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The revert arrow and the value the app starts with must be the same value. The scenarios
/// that read the values back out of a running app belong to the app-state suites; here the
/// constants and their stored spellings are pinned.
/// </summary>
public sealed class SettingsDefaultsTests
{
    /// <summary>
    /// A fresh app is already at its defaults, so every arrow starts inactive. The dictation key
    /// default is this port's own - Right Ctrl - and must agree with what the settings reader
    /// falls back to.
    /// </summary>
    [Fact]
    public void TestScenario001()
    {
        Assert.Equal(DictationHotkey.RightCtrl, SettingsDefaults.Hotkey);
        Assert.Contains(SettingsDefaults.Hotkey, DictationHotkeyInfo.AllCases);
        Assert.Null(SettingsDefaults.CopyShortcut);
        Assert.True(SettingsDefaults.SoundsEnabled);
        Assert.False(SettingsDefaults.CopiesToClipboard);
        Assert.False(SettingsDefaults.AppendsTrailingSpace);
        Assert.False(SettingsDefaults.LaunchAtLogin);
        Assert.False(SettingsDefaults.LearnFromEdits);
        Assert.False(SettingsDefaults.StopsOnSilence);
        Assert.True(SettingsDefaults.DetailedLogging);
        Assert.Equal(DictationOverlayPlacement.Top, SettingsDefaults.OverlayPlacement);
        Assert.Equal(AppAppearance.System, SettingsDefaults.Appearance);
        Assert.Null(SettingsDefaults.InputDeviceUid);
        Assert.Equal(5, SettingsDefaults.HistoryLimit);
    }

    /// <summary>The theme survives a relaunch, or it is not a setting.</summary>
    [Fact]
    public void TestScenario003()
    {
        Assert.Equal(AppAppearance.Dark, AppAppearances.Parse("dark"));
        Assert.Equal(AppAppearance.Light, AppAppearances.Parse("light"));
        Assert.Equal(AppAppearance.System, AppAppearances.Parse("system"));
        Assert.Equal(SettingsDefaults.Appearance, AppAppearances.Parse(null));
        Assert.Equal(SettingsDefaults.Appearance, AppAppearances.Parse("sepia"));
        foreach (AppAppearance appearance in AppAppearances.AllCases)
        {
            Assert.Equal(appearance, AppAppearances.Parse(appearance.RawValue));
            Assert.False(string.IsNullOrEmpty(appearance.Title));
        }
    }
}
