using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The tray's recording rows (upstream MenuSectionsTests' recording cases and
/// MenuBarStatus.recordingLine), and the dot a recording puts on the icon.
/// </summary>
public sealed class RecordingMenuTests
{
    private static MenuRow[][] Sections(
        DictationState state = DictationState.Idle,
        MeetingRecordingState recording = MeetingRecordingState.Idle,
        bool canRecord = true,
        bool systemAudio = true,
        bool recoveredText = false) =>
        MenuSections.Sections(state, true, recoveredText, false, recording, canRecord, systemAudio)
            .Select(section => section.ToArray())
            .ToArray();

    [Fact]
    public void AnIdleAppOffersBothKindsOfRecording()
    {
        Assert.Equal(
            [
                [MenuRow.StatusLine],
                [MenuRow.StartRecording, MenuRow.StartVoiceNote],
                [MenuRow.Settings, MenuRow.Quit],
            ],
            Sections());
    }

    /// <summary>Without system audio "Start Recording" already is a voice note; a second row would repeat it.</summary>
    [Fact]
    public void WithoutSystemAudioThereIsOneRecordRow()
    {
        Assert.Equal([MenuRow.StartRecording], Sections(systemAudio: false)[1]);
    }

    [Fact]
    public void ABuildThatCannotRecordOffersNothing()
    {
        Assert.DoesNotContain(
            Sections(canRecord: false).SelectMany(section => section),
            row => row is MenuRow.StartRecording or MenuRow.StartVoiceNote);
    }

    /// <summary>The recording's line replaces the idle status line and leads the menu.</summary>
    [Fact]
    public void ARunningRecordingLeadsWithItsLinePauseAndStop()
    {
        Assert.Equal(
            [
                [MenuRow.RecordingLine, MenuRow.PauseRecording, MenuRow.StopRecording],
                [MenuRow.Settings, MenuRow.Quit],
            ],
            Sections(recording: MeetingRecordingState.Recording));
        Assert.Equal(
            [MenuRow.RecordingLine, MenuRow.ResumeRecording, MenuRow.StopRecording],
            Sections(recording: MeetingRecordingState.Paused)[0]);
    }

    [Fact]
    public void StartingAndStoppingShowOnlyTheLine()
    {
        foreach (MeetingRecordingState busy in new[] { MeetingRecordingState.Starting, MeetingRecordingState.Stopping })
        {
            Assert.Equal([[MenuRow.RecordingLine], [MenuRow.Settings, MenuRow.Quit]], Sections(recording: busy));
        }
    }

    /// <summary>A recording is orthogonal to a dictation: both sections at once, the recording first.</summary>
    [Fact]
    public void ADictationDuringARecordingKeepsBoth()
    {
        Assert.Equal(
            [
                [MenuRow.StatusLine],
                [MenuRow.RecordingLine, MenuRow.PauseRecording, MenuRow.StopRecording],
                [MenuRow.StopAndInsert, MenuRow.CancelDictation],
                [MenuRow.Settings, MenuRow.Quit],
            ],
            Sections(DictationState.Listening, MeetingRecordingState.Recording));
    }

    [Fact]
    public void WaitingWordsKeepTheStatusLineDuringARecording()
    {
        Assert.Equal(MenuRow.StatusLine, Sections(recording: MeetingRecordingState.Recording, recoveredText: true)[0][0]);
    }

    [Fact]
    public void TheStartAndStopRowsCarryTheShortcut()
    {
        Assert.Equal("Start Recording   Ctrl+Alt+R", MenuSections.RecordingTitle(MenuRow.StartRecording, MeetingRecordingState.Idle, TimeSpan.Zero, "Ctrl+Alt+R"));
        Assert.Equal("Stop Recording   Ctrl+Alt+R", MenuSections.RecordingTitle(MenuRow.StopRecording, MeetingRecordingState.Recording, TimeSpan.Zero, "Ctrl+Alt+R"));
        Assert.Equal("Start Recording", MenuSections.RecordingTitle(MenuRow.StartRecording, MeetingRecordingState.Idle, TimeSpan.Zero));
        Assert.Equal("Record Voice Note", MenuSections.RecordingTitle(MenuRow.StartVoiceNote, MeetingRecordingState.Idle, TimeSpan.Zero, "Ctrl+Alt+R"));
    }

    [Fact]
    public void TheLineSaysWhatIsHappeningAndForHowLong()
    {
        TimeSpan elapsed = TimeSpan.FromSeconds(754);
        string Line(MeetingRecordingState state, bool degraded = false, bool missing = false) =>
            MenuSections.RecordingTitle(MenuRow.RecordingLine, state, elapsed, null, degraded, missing);

        Assert.Equal("Recording — 12:34", Line(MeetingRecordingState.Recording));
        Assert.Equal("Paused — 12:34", Line(MeetingRecordingState.Paused, degraded: true));
        Assert.Equal("Recording — 12:34 — only your microphone", Line(MeetingRecordingState.Recording, degraded: true));
        Assert.Equal("Recording — 12:34 — not your microphone", Line(MeetingRecordingState.Recording, missing: true));
        Assert.Equal("Recording — 12:34 — nothing arriving", Line(MeetingRecordingState.Recording, true, true));
        Assert.Equal("Starting recording…", Line(MeetingRecordingState.Starting));
        Assert.Equal("Saving recording…", Line(MeetingRecordingState.Stopping));
    }

    /// <summary>The shortcut never raises a window: the red dot is its answer.</summary>
    [Fact]
    public void ARecordingPutsTheRedDotOnTheIcon()
    {
        Assert.Equal(TrayActivity.Recording, TrayStatus.Activity(DictationState.Idle, isRecordingMeeting: true));
        Assert.Equal(TrayActivity.Recording, TrayStatus.Activity(DictationState.Transcribing, isRecordingMeeting: true));
        Assert.Equal(TrayBadge.Attention, TrayStatus.Badge(TrayActivity.Recording, recordingIsDegraded: true));
    }
}
