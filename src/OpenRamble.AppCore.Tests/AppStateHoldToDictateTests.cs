using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The two ways a hold-to-dictate gesture can start, and the one machine behind them.
///
/// On Windows the dictation key can be eaten before the process ever sees it - a virtual machine
/// client, a remote desktop, an elevated window in front - and from the outside that is
/// indistinguishable from a product that does not work. The on-screen hold is the way through it,
/// and it is only worth anything if it is the same dictation: a second entry point with its own
/// readiness rules would agree with the key exactly until one of the two was changed.
/// </summary>
public sealed class AppStateHoldToDictateTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    private FakeCapture Capture => harness.Capture;

    public void Dispose() => harness.Dispose();

    /// <summary>The application with the permission granted and the model laid out.</summary>
    private AppState ReadyState()
    {
        harness.InstallModelMarker();
        harness.Permissions.MicrophoneGranted = true;
        AppState state = harness.MakeState();
        return state;
    }

    /// <summary>
    /// The button opens the microphone and closes it, exactly as the key does.
    /// </summary>
    [Fact]
    public async Task TheButtonRunsTheSameSessionTheKeyRuns()
    {
        AppState state = ReadyState();
        await TestWait.For("the model was seen on disk", () => state.ModelStateValue.IsReady);

        state.PressDictationButton();

        Assert.True(state.IsDictationButtonHeld);
        await TestWait.For("the microphone opened", () => Capture.IsRecording);
        await TestWait.For("recording started", () => state.DictationStateValue == DictationState.Listening);

        state.ReleaseDictationButton();

        Assert.False(state.IsDictationButtonHeld);
        await TestWait.For("the session finished", () => state.DictationStateValue == DictationState.Idle);
        Assert.False(Capture.IsRecording);
        Assert.Equal(1, Capture.StartCount);
    }

    /// <summary>
    /// The badge belongs to the key and to nothing else.
    ///
    /// The try-out step exists to answer one question - does the key reach OpenRamble - and it
    /// answers it with these two values. A mouse press that moved them would make the one screen
    /// built to detect a dead keyboard path report that the path is fine.
    /// </summary>
    [Fact]
    public async Task TheButtonIsNeverEvidenceAboutTheKey()
    {
        AppState state = ReadyState();
        await TestWait.For("the model was seen on disk", () => state.ModelStateValue.IsReady);

        state.PressDictationButton();
        await TestWait.For("recording started", () => state.DictationStateValue == DictationState.Listening);

        Assert.False(state.IsHotkeyHeld);
        Assert.Equal(0, state.HotkeyPressCount);

        state.ReleaseDictationButton();
        await TestWait.For("the session finished", () => state.DictationStateValue == DictationState.Idle);

        Assert.Equal(0, state.HotkeyPressCount);
    }

    /// <summary>
    /// A release without a press, and a second press while held, both do nothing.
    ///
    /// Both arrive in life: the pointer leaves the button, the window loses capture, the window
    /// closes mid-hold. Each of those has to be able to end the hold, so each of them calls the
    /// release - and the ones that arrive after the first must not stop the next dictation.
    /// </summary>
    [Fact]
    public async Task RepeatedEdgesAreHarmless()
    {
        AppState state = ReadyState();
        await TestWait.For("the model was seen on disk", () => state.ModelStateValue.IsReady);

        state.ReleaseDictationButton();
        Assert.False(state.IsDictationButtonHeld);
        Assert.Equal(0, Capture.StartCount);

        state.PressDictationButton();
        state.PressDictationButton();
        await TestWait.For("recording started", () => state.DictationStateValue == DictationState.Listening);
        Assert.Equal(1, Capture.StartCount);

        state.ReleaseDictationButton();
        state.ReleaseDictationButton();
        await TestWait.For("the session finished", () => state.DictationStateValue == DictationState.Idle);
        Assert.Equal(1, Capture.StopCount);
    }

    /// <summary>
    /// The button obeys the same readiness rules: it explains instead of starting.
    ///
    /// A control that recorded regardless would take a person's sentence and drop it - the exact
    /// silent loss the readiness explanation exists to prevent.
    /// </summary>
    [Fact]
    public async Task TheButtonExplainsItselfWhenDictationCannotStart()
    {
        harness.Permissions.MicrophoneGranted = false;
        AppState state = harness.MakeState();

        state.PressDictationButton();

        await TestWait.For(
            "the reason was shown",
            () => harness.Overlay.Notices.Any(notice =>
                notice.Message.Contains("microphone access", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, Capture.StartCount);
        Assert.Equal(DictationState.Idle, state.DictationStateValue);

        state.ReleaseDictationButton();
        Assert.Equal(DictationState.Idle, state.DictationStateValue);
    }

    /// <summary>
    /// A hook the system refused is remembered, not just announced.
    ///
    /// The notice that reports it lives four seconds; the dead hook lives for the rest of the
    /// process. Without a kept reason the try-out shows a grey badge and "nothing has reached
    /// OpenRamble yet" forever - which is what a person who is not pressing anything also sees.
    /// </summary>
    [Fact]
    public void TrackingThatFailedToStartIsRememberedForTheTryOut()
    {
        AppState state = harness.MakeState();
        Assert.Null(state.HotkeyTrackingFailure);

        Monitor.OnTrackingFailure!("OpenRamble couldn't start watching the dictation key.");

        Assert.Equal("OpenRamble couldn't start watching the dictation key.", state.HotkeyTrackingFailure);
        // And it is on screen once, immediately: the kept copy is for the step, not instead of it.
        Assert.Contains(
            harness.Overlay.Notices,
            notice => notice.Message.Contains("watching the dictation key", StringComparison.Ordinal));
    }

    /// <summary>
    /// Picking a key inside the first-launch flow arms it at once and survives a restart.
    ///
    /// The picker is on the try-out page precisely because that is where a key that does not
    /// arrive is discovered; a choice that only took effect after a restart would leave the person
    /// on the same dead page they were sent there to escape.
    /// </summary>
    [Fact]
    public void ChoosingAKeyArmsTheHookAndIsRemembered()
    {
        AppState state = harness.MakeState();

        state.Hotkey = DictationHotkey.RightShift;

        // The live hook is re-armed, so the very next hold on the same page uses the new key.
        Assert.Equal(DictationHotkey.RightShift, Monitor.Hotkey);
        Assert.Equal(nameof(DictationHotkey.RightShift), harness.Settings.Read().Hotkey);

        // And a later launch reads the same key back: one source of truth, one file, both pickers.
        AppState again = harness.MakeState();
        Assert.Equal(DictationHotkey.RightShift, again.Hotkey);
    }
}
