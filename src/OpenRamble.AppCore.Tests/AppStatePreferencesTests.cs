using DictationCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The settings that arrived with upstream 0.12–0.22, driven through a real <see cref="AppState"/>:
/// the copy shortcut, the clipboard copy, the trailing space, the microphone picker, the panel
/// placement, the theme, the silence rule and the log file.
/// </summary>
/// <remarks>
/// Ported from the macOS <c>CopyHotkeyTests</c>, <c>SettingsDefaultsTests</c>,
/// <c>AudioInputDevicesTests</c>, <c>OverlayPlacementTests</c> and <c>TrailingSpaceTests</c>
/// scenarios that need a running app; the pure halves live beside the types they test. The
/// macOS "without Accessibility the watcher does not start" scenario has no counterpart: the hook
/// here needs no permission.
/// </remarks>
public sealed class AppStatePreferencesTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    private static readonly KeyCombination CtrlAltC = new(67, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    public void Dispose() => harness.Dispose();

    // MARK: - Defaults

    /// <summary>
    /// A fresh app is already at its defaults, so every revert arrow starts inactive.
    ///
    /// This is the whole contract of the arrow: if a default declared in
    /// <see cref="SettingsDefaults"/> differed from what the app actually reads at launch, the
    /// arrow would move the setting somewhere the person never chose - and it would look like a
    /// reset while doing it.
    /// </summary>
    [Fact]
    public void AFreshAppStartsAtEveryDefault()
    {
        AppState state = harness.MakeState();

        Assert.Equal(SettingsDefaults.Hotkey, state.Hotkey);
        Assert.Equal(SettingsDefaults.CopyShortcut, state.CopyShortcut);
        Assert.Equal(SettingsDefaults.SoundsEnabled, state.SoundsEnabled);
        Assert.Equal(SettingsDefaults.CopiesToClipboard, state.CopiesToClipboard);
        Assert.Equal(SettingsDefaults.AppendsTrailingSpace, state.AppendsTrailingSpace);
        Assert.Equal(SettingsDefaults.OverlayPlacement, state.OverlayPlacement);
        Assert.Equal(SettingsDefaults.Appearance, state.Appearance);
        Assert.Equal(SettingsDefaults.StopsOnSilence, state.StopsOnSilence);
        Assert.Equal(SettingsDefaults.DetailedLogging, state.DetailedLogging);
        Assert.Equal(SettingsDefaults.InputDeviceUid, state.InputDeviceUid);
        Assert.Equal(SettingsDefaults.LearnFromEdits, state.LearnFromEdits);
    }

    /// <summary>Changing a setting and reverting it returns exactly the start value.</summary>
    [Fact]
    public void ChangingAndRevertingASettingReturnsTheStartValue()
    {
        AppState state = harness.MakeState();
        state.AppendsTrailingSpace = true;
        state.OverlayPlacement = DictationOverlayPlacement.Bottom;
        Assert.NotEqual(SettingsDefaults.AppendsTrailingSpace, state.AppendsTrailingSpace);

        state.AppendsTrailingSpace = SettingsDefaults.AppendsTrailingSpace;
        state.OverlayPlacement = SettingsDefaults.OverlayPlacement;

        Assert.Equal(SettingsDefaults.AppendsTrailingSpace, state.AppendsTrailingSpace);
        Assert.Equal(SettingsDefaults.OverlayPlacement, state.OverlayPlacement);
    }

    /// <summary>The theme survives a relaunch, or it is not a setting.</summary>
    [Fact]
    public void TheThemeSurvivesARelaunch()
    {
        harness.Settings.Write(new AppSettings { Appearance = "dark" });
        Assert.Equal(AppAppearance.Dark, harness.MakeState().Appearance);

        harness.MakeState().Appearance = AppAppearance.Light;
        Assert.Equal("light", harness.Settings.Read().Appearance);
    }

    /// <summary>A settings file from the future does not turn into something the person never chose.</summary>
    [Fact]
    public void AnUnknownStoredValueReadsAsTheDefault()
    {
        harness.Settings.Write(new AppSettings
        {
            Appearance = "sepia",
            OverlayPlacement = "sideways",
            CopyShortcut = "nonsense",
        });

        AppState state = harness.MakeState();

        Assert.Equal(SettingsDefaults.Appearance, state.Appearance);
        Assert.Equal(SettingsDefaults.OverlayPlacement, state.OverlayPlacement);
        Assert.Null(state.CopyShortcut);
    }

    // MARK: - The copy shortcut

    /// <summary>Nobody gets a second global shortcut without asking for one.</summary>
    [Fact]
    public void NobodyGetsASecondShortcutWithoutAskingForOne()
    {
        AppState state = harness.MakeState();
        Assert.Null(state.CopyShortcut);
        Assert.False(state.CopiesToClipboard);
        Assert.False(harness.CopyMonitor.IsRunning);
    }

    /// <summary>
    /// The chosen shortcut reaches the watcher, and clearing it takes the shortcut back off the
    /// keyboard.
    /// </summary>
    [Fact]
    public void TheChosenShortcutReachesTheWatcherAndClearingTakesItBack()
    {
        AppState state = harness.MakeState();

        state.CopyShortcut = CtrlAltC;
        Assert.Equal(CtrlAltC, harness.CopyMonitor.Shortcut);
        Assert.True(harness.CopyMonitor.IsRunning);

        state.CopyShortcut = null;
        Assert.False(harness.CopyMonitor.IsRunning);
    }

    /// <summary>
    /// It comes back after a relaunch, because a shortcut you have to set again every morning is
    /// not a shortcut - and it is on the keyboard from the first moment of the run.
    /// </summary>
    [Fact]
    public void TheShortcutComesBackAfterARelaunch()
    {
        harness.Settings.Write(new AppSettings { CopyShortcut = CtrlAltC.RawValue });

        AppState state = harness.MakeState();

        Assert.Equal(CtrlAltC, state.CopyShortcut);
        Assert.Equal(CtrlAltC, harness.CopyMonitor.Shortcut);
        Assert.True(harness.CopyMonitor.IsRunning);
    }

    /// <summary>Pressing it with nothing dictated says so rather than copying silence.</summary>
    [Fact]
    public async Task PressingItWithNothingDictatedSaysSo()
    {
        AppState state = harness.MakeState();
        state.CopyShortcut = CtrlAltC;

        harness.CopyMonitor.Press();

        await TestWait.For("the notice reached the panel", () => harness.Overlay.Notices.Count > 0);
        Assert.Null(state.LastDictationValue);
        Assert.Empty(harness.Clipboard.Copied);
        Assert.Equal("Nothing dictated yet.", harness.Overlay.Notices[^1].Message);
    }

    /// <summary>The key puts the text that was inserted back on the clipboard, and says so.</summary>
    [Fact]
    public async Task TheShortcutCopiesTheLastDictation()
    {
        AppState state = await ReadyState();
        state.CopyShortcut = CtrlAltC;
        await DictateOnce(state);

        harness.CopyMonitor.Press();

        Assert.Equal(["Ping test"], harness.Clipboard.Copied);
        await TestWait.For(
            "the copy was confirmed",
            () => harness.Overlay.Notices.Any(notice => notice.Message.StartsWith("Copied", StringComparison.Ordinal)));
    }

    /// <summary>The menu row carries the binding, because nothing else on the screen can.</summary>
    [Fact]
    public void TheMenuRowShowsTheShortcut()
    {
        AppState state = harness.MakeState();
        Assert.Equal("Copy Last Dictation", state.CopyLastMenuTitle);

        state.CopyShortcut = CtrlAltC;

        Assert.Equal("Copy Last Dictation   Ctrl+Alt+C", state.CopyLastMenuTitle);
    }

    /// <summary>
    /// Waking is where the hotkey monitor is restarted to forget a release that never arrived;
    /// the second watcher goes through the same door and comes back with its shortcut intact.
    /// </summary>
    [Fact]
    public void TheWatcherSurvivesSleep()
    {
        AppState state = harness.MakeState();
        state.CopyShortcut = CtrlAltC;

        harness.SystemEvents.PostDidWake();

        Assert.True(harness.CopyMonitor.IsRunning);
        Assert.Equal(CtrlAltC, harness.CopyMonitor.Shortcut);
    }

    // MARK: - Dictations on the clipboard

    /// <summary>
    /// Off by default because the clipboard is shared with everything else running here; on,
    /// every finished dictation is also left there.
    /// </summary>
    [Fact]
    public async Task AFinishedDictationIsLeftOnTheClipboardOnlyWhenAsked()
    {
        AppState state = await ReadyState();

        await DictateOnce(state);
        Assert.Empty(harness.Clipboard.Copied);

        state.CopiesToClipboard = true;
        await DictateOnce(state);
        Assert.Equal(["Ping test"], harness.Clipboard.Copied);
    }

    // MARK: - The trailing space

    /// <summary>For dictating in runs: the next phrase must not arrive welded to the last word.</summary>
    [Fact]
    public async Task TheTrailingSpaceFollowsTheSetting()
    {
        AppState state = await ReadyState();

        await DictateOnce(state);
        Assert.Equal("Ping test", harness.Inserter.InsertedTexts[^1]);

        state.AppendsTrailingSpace = true;
        await DictateOnce(state);
        Assert.Equal("Ping test ", harness.Inserter.InsertedTexts[^1]);
        // Provenance describes the text that was inserted, or the edit watcher would compare
        // against something that never reached the screen.
        Assert.Equal("Ping test ", state.LastDictationValue?.Provenance.FinalText);
    }

    // MARK: - The microphone

    /// <summary>The default is the system's, which is what almost everyone wants.</summary>
    [Fact]
    public void TheDefaultMicrophoneIsTheSystems()
    {
        AppState state = harness.MakeState();
        Assert.Null(state.InputDeviceUid);
        Assert.Null(state.InputDeviceNotice);
    }

    /// <summary>
    /// A microphone that is not on this machine says so rather than being swapped in silence.
    /// </summary>
    [Fact]
    public void AMissingMicrophoneIsSaidOutLoud()
    {
        harness.InputDevices.Devices.Add(new AudioInputDevice("usb-headset", "USB Headset"));
        AppState state = harness.MakeState();

        state.InputDeviceUid = "usb-headset";
        Assert.Null(state.InputDeviceNotice);
        Assert.Equal(["USB Headset"], state.AvailableInputDevices.Select(device => device.Name));

        state.InputDeviceUid = "a-device-that-is-not-here";
        Assert.NotNull(state.InputDeviceNotice);
    }

    /// <summary>It survives a relaunch, or it is not a setting.</summary>
    [Fact]
    public void TheMicrophoneChoiceSurvivesARelaunch()
    {
        harness.Settings.Write(new AppSettings { InputDeviceUid = "some-uid" });
        Assert.Equal("some-uid", harness.MakeState().InputDeviceUid);
    }

    // MARK: - The panel

    [Fact]
    public void ThePlacementDefaultsToTopAndPersists()
    {
        AppState state = harness.MakeState();
        Assert.Equal(DictationOverlayPlacement.Top, state.OverlayPlacement);

        state.OverlayPlacement = DictationOverlayPlacement.Bottom;

        Assert.Equal("bottom", harness.Settings.Read().OverlayPlacement);
        Assert.Equal(DictationOverlayPlacement.Bottom, harness.MakeState().OverlayPlacement);
    }

    // MARK: - Stopping on silence

    /// <summary>
    /// Hands-free, the quiet after speech ends the take. Silence before a word ends nothing, and
    /// the quiet clock restarts on the next word.
    /// </summary>
    [Fact]
    public async Task TwoSecondsOfQuietFinishAHandsFreeDictation()
    {
        AppState state = await ReadyState();
        state.StopsOnSilence = true;
        Monitor.OnDoubleTap!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        // A quiet room before the first word ends nothing.
        Feed(0.001f, TimeSpan.FromSeconds(3));
        Assert.Equal(DictationState.Listening, state.DictationStateValue);

        Feed(0.5f, TimeSpan.Zero);
        Feed(0.001f, TimeSpan.FromSeconds(1));
        // A word restarts the clock.
        Feed(0.5f, TimeSpan.Zero);
        Feed(0.001f, TimeSpan.FromSeconds(1.5));
        Assert.Equal(DictationState.Listening, state.DictationStateValue);

        // Two seconds of quiet since it was first heard.
        Feed(0.001f, TimeSpan.FromSeconds(2.1));

        await TestWait.For("the take finished by itself", () => state.DictationStateValue == DictationState.Idle);
        Assert.False(harness.Capture.IsRecording);
    }

    /// <summary>While the key is held the person is holding it: a pause is never an ending.</summary>
    [Fact]
    public async Task AHeldKeyIsNeverStoppedBySilence()
    {
        AppState state = await ReadyState();
        state.StopsOnSilence = true;
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        Feed(0.5f, TimeSpan.Zero);
        Feed(0.001f, TimeSpan.FromSeconds(1));
        Feed(0.001f, TimeSpan.FromSeconds(3));
        await Task.Delay(30);

        Assert.Equal(DictationState.Listening, state.DictationStateValue);
        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }

    /// <summary>Off by default, and off means the quiet is never even counted.</summary>
    [Fact]
    public async Task TheRuleIsOffUnlessChosen()
    {
        AppState state = await ReadyState();
        Monitor.OnDoubleTap!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        Feed(0.5f, TimeSpan.Zero);
        Feed(0.001f, TimeSpan.FromSeconds(1));
        Feed(0.001f, TimeSpan.FromSeconds(3));
        await Task.Delay(30);

        Assert.Equal(DictationState.Listening, state.DictationStateValue);
        state.FinishCurrentDictation();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }

    // MARK: - The log file

    /// <summary>
    /// A switch writes a plain text file a person can send; while it is off nothing is written.
    /// Timings and reasons only - the line has no room for words, by construction.
    /// </summary>
    [Fact]
    public async Task TheDetailedLogExistsOnlyWhenAsked()
    {
        AppState state = await ReadyState();
        await DictateOnce(state);
        Assert.False(Directory.Exists(state.LogDirectory), "nothing is written while the switch is off");

        state.DetailedLogging = true;
        await DictateOnce(state);

        await TestWait.For("the line reached the file", () => LogText().Contains("dictation total=", StringComparison.Ordinal));
        Assert.DoesNotContain("Ping test", LogText(), StringComparison.Ordinal);
        Assert.True(harness.Settings.Read().DetailedLogging);
    }

    // MARK: - Helpers

    private string LogText()
    {
        if (!Directory.Exists(harness.MakeStateLogDirectory())) { return string.Empty; }
        return string.Concat(Directory.GetFiles(harness.MakeStateLogDirectory(), "*.log").Select(File.ReadAllText));
    }

    /// <summary>One frame of sound, <paramref name="later"/> after the previous one.</summary>
    private void Feed(float peak, TimeSpan later)
    {
        harness.MonotonicNow = harness.MonotonicNow.Advanced(later);
        harness.OnSamples!([peak]);
    }

    private async Task<AppState> ReadyState()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.True(state.IsDictationReady);
        return state;
    }

    private async Task DictateOnce(AppState state)
    {
        int before = state.SuccessfulDictationCount;
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("the text was inserted", () => state.SuccessfulDictationCount > before);
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }
}
