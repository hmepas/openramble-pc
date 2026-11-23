using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The global chord that starts and stops a recording (upstream RecordingHotkeyTests, adapted:
/// the chord is matched by the shared shortcut monitor here, so what is left is the setting and
/// what a press does).
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateRecordingShortcutTests : IDisposable
{
    private static readonly KeyCombination CtrlAltR = new(19, HotkeyModifiers.Control | HotkeyModifiers.Alt);
    private static readonly KeyCombination CtrlAltM = new(50, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    private readonly AppHarness harness = new() { DefaultRecordingShortcut = CtrlAltR };

    public void Dispose() => harness.Dispose();

    [Fact]
    public void NeverChosenReadsAsTheDefaultAndIsOnTheKeyboard()
    {
        AppState state = harness.MakeState();

        Assert.Equal(CtrlAltR, state.DefaultRecordingShortcut);
        Assert.Equal(CtrlAltR, state.RecordingShortcut);
        Assert.Equal(CtrlAltR, harness.RecordMonitor.Shortcut);
        Assert.True(harness.RecordMonitor.IsRunning);
    }

    [Fact]
    public void APlatformWithoutADefaultHasNoChord()
    {
        harness.DefaultRecordingShortcut = null;
        AppState state = harness.MakeState();

        Assert.Null(state.RecordingShortcut);
        Assert.False(harness.RecordMonitor.IsRunning);
    }

    [Fact]
    public void AChosenChordComesBackAfterARelaunch()
    {
        AppState first = harness.MakeState();
        first.RecordingShortcut = CtrlAltM;
        Assert.Equal(CtrlAltM, harness.RecordMonitor.Shortcut);
        first.Dispose();

        AppState second = harness.MakeState();
        Assert.Equal(CtrlAltM, second.RecordingShortcut);
        Assert.NotEqual(second.DefaultRecordingShortcut, second.RecordingShortcut);
    }

    /// <summary>Cleared is a choice: it stays cleared, rather than reading as "never chosen".</summary>
    [Fact]
    public void AClearedChordStaysClearedAfterARelaunch()
    {
        AppState first = harness.MakeState();
        first.RecordingShortcut = null;
        Assert.False(harness.RecordMonitor.IsRunning);
        first.Dispose();

        AppState second = harness.MakeState();
        Assert.Null(second.RecordingShortcut);
        Assert.False(harness.RecordMonitor.IsRunning);
    }

    [Fact]
    public void TheChordIsStoredApartFromThePasteChord()
    {
        AppState state = harness.MakeState();
        state.RecordingShortcut = CtrlAltM;

        Assert.Equal(CtrlAltM.RawValue, harness.Settings.Read().RecordingShortcut);
        Assert.Null(harness.Settings.Read().CopyShortcut);
        Assert.Null(state.LastTakeShortcut);
    }

    [Fact]
    public async Task APressStartsARecordingOfBothSidesAndTheNextStopsIt()
    {
        AppState state = harness.MakeState();

        harness.RecordMonitor.Press();
        await TestWait.For("recording", () => state.RecordingState == MeetingRecordingState.Recording);
        Assert.True(harness.MeetingCapture.IncludesSystemAudio);

        harness.RecordMonitor.Press();
        await TestWait.For("filed", () => state.RecordingState == MeetingRecordingState.Idle && state.Recordings.Count == 1);
        Assert.Equal(1, harness.MeetingCapture.StartCount);
        Assert.Equal(1, harness.MeetingCapture.StopCount);
    }

    [Fact]
    public async Task APressWhilePausedStops()
    {
        AppState state = harness.MakeState();
        await state.StartRecording();
        await state.PauseRecording();

        harness.RecordMonitor.Press();

        await TestWait.For("filed", () => state.RecordingState == MeetingRecordingState.Idle && state.Recordings.Count == 1);
    }

    [Fact]
    public void TheWatcherSurvivesSleep()
    {
        AppState state = harness.MakeState();
        state.RecordingShortcut = CtrlAltM;

        harness.SystemEvents.PostDidWake();

        Assert.True(harness.RecordMonitor.IsRunning);
        Assert.Equal(CtrlAltM, harness.RecordMonitor.Shortcut);
    }
}
