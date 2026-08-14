using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The dictation panel is the only feedback channel during dictation.
///
/// The application has no window of its own: if the panel is silent, the person learns nothing.
/// What is written on it and what is said about it out loud is what gets checked.
/// </summary>
public sealed class OverlayContentTests
{
    private static OverlayContent Content(
        DictationState state,
        DictationNotice? notice = null,
        double elapsed = 0) =>
        OverlayContent.Make(state, notice, TimeSpan.FromSeconds(elapsed));

    // MARK: - States

    [Fact]
    public void Scenario001()
    {
        Assert.Equal("Done", Content(DictationState.Idle).Title);
        Assert.Equal("Turning on the microphone…", Content(DictationState.Preparing).Title);
        Assert.Equal("Listening", Content(DictationState.Listening).Title);
        Assert.Equal("Transcribing…", Content(DictationState.Transcribing).Title);
        Assert.Equal("Done", Content(DictationState.Inserting).Title);
    }

    [Fact]
    public void Scenario002()
    {
        Assert.Equal(OverlayTone.Recording, Content(DictationState.Listening).Tone);
        Assert.Equal(OverlayTone.Working, Content(DictationState.Preparing).Tone);
        Assert.Equal(OverlayTone.Working, Content(DictationState.Transcribing).Tone);
        Assert.Equal(OverlayTone.Idle, Content(DictationState.Inserting).Tone);
        Assert.Equal(OverlayTone.Idle, Content(DictationState.Idle).Tone);
    }

    // MARK: - Seconds

    /// <summary>
    /// Duration is drawn beside the live waveform by the view. The content model does not present
    /// the finished recording length as if it were transcription progress.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        Assert.Null(Content(DictationState.Listening, elapsed: 7).Subtitle);
        Assert.Null(Content(DictationState.Transcribing, elapsed: 12.4).Subtitle);
        Assert.Null(Content(DictationState.Preparing, elapsed: 3).Subtitle);
        Assert.Null(Content(DictationState.Inserting, elapsed: 3).Subtitle);
        Assert.Null(Content(DictationState.Idle, elapsed: 3).Subtitle);
    }

    [Fact]
    public void Scenario004()
    {
        Assert.Null(Content(DictationState.Listening, elapsed: -2).Subtitle);
        Assert.Equal("Recording, 0 seconds.", Content(DictationState.Listening, elapsed: -2).AccessibilityLabel);
    }

    /// <summary>"5 s" is read by a screen reader as "5 es".</summary>
    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(2, "2 seconds")]
    [InlineData(4, "4 seconds")]
    [InlineData(5, "5 seconds")]
    [InlineData(11, "11 seconds")]
    [InlineData(12, "12 seconds")]
    [InlineData(21, "21 seconds")]
    [InlineData(22, "22 seconds")]
    [InlineData(25, "25 seconds")]
    [InlineData(111, "111 seconds")]
    [InlineData(0, "0 seconds")]
    public void Scenario005(int seconds, string expected)
    {
        Assert.Equal(expected, OverlayContent.SpokenSeconds(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Scenario006()
    {
        Assert.Equal("Recording, 3 seconds.", Content(DictationState.Listening, elapsed: 3).AccessibilityLabel);
        Assert.Equal("Recording, 1 second.", Content(DictationState.Listening, elapsed: 1).AccessibilityLabel);
    }

    // MARK: - Announcements

    /// <summary>
    /// The main announcement in the whole application.
    ///
    /// The panel does not take focus on purpose, there is no taskbar window, there is no window
    /// at all: without this phrase a blind person does not know the microphone is on.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        OverlayContent content = Content(DictationState.Listening);

        Assert.Equal("Recording.", content.Announcement);
        Assert.True(content.IsAnnouncementUrgent);
    }

    [Fact]
    public void Scenario008()
    {
        Assert.Equal(
            "Recording stopped, transcribing speech",
            Content(DictationState.Transcribing).Announcement);
        Assert.Null(Content(DictationState.Inserting).Announcement);
        Assert.Equal("Turning on the microphone", Content(DictationState.Preparing).Announcement);
    }

    /// <summary>The panel leaves the screen at rest - there is nothing to announce there.</summary>
    [Fact]
    public void Scenario009()
    {
        Assert.Null(Content(DictationState.Idle).Announcement);
    }

    // MARK: - Messages

    [Fact]
    public void Scenario010()
    {
        var notice = new DictationNotice(
            DictationNoticeKind.Warning,
            "Text not inserted: the target window is protected.");
        OverlayContent content = Content(DictationState.Listening, notice, elapsed: 9);

        Assert.Equal(notice.Message, content.Title);
        // The counter next to the message is dropped: the recording is no longer running.
        Assert.Null(content.Subtitle);
        Assert.Equal(OverlayTone.Warning, content.Tone);
        Assert.Equal(notice.Message, content.Announcement);
    }

    [Fact]
    public void Scenario011()
    {
        Assert.Equal(
            OverlayTone.Info,
            Content(DictationState.Idle, new DictationNotice(DictationNoticeKind.Info, "a")).Tone);
        Assert.Equal(
            OverlayTone.Warning,
            Content(DictationState.Idle, new DictationNotice(DictationNoticeKind.Warning, "b")).Tone);
        Assert.Equal(
            OverlayTone.Failure,
            Content(DictationState.Idle, new DictationNotice(DictationNoticeKind.Failure, "c")).Tone);
    }

    /// <summary>A failure interrupts what the screen reader is reading; a plain notice does not.</summary>
    [Fact]
    public void Scenario012()
    {
        Assert.False(
            Content(DictationState.Idle, new DictationNotice(DictationNoticeKind.Info, "a")).IsAnnouncementUrgent);
        Assert.True(
            Content(DictationState.Idle, new DictationNotice(DictationNoticeKind.Warning, "b")).IsAnnouncementUrgent);
        Assert.True(
            Content(DictationState.Idle, new DictationNotice(DictationNoticeKind.Failure, "c")).IsAnnouncementUrgent);
    }

    // MARK: - Labels

    [Fact]
    public void Scenario013()
    {
        DictationState[] states =
        [
            DictationState.Idle,
            DictationState.Preparing,
            DictationState.Listening,
            DictationState.Transcribing,
            DictationState.Inserting,
        ];

        foreach (DictationState state in states)
        {
            Assert.False(
                string.IsNullOrEmpty(Content(state).AccessibilityLabel),
                $"state {state} is not passed on to the screen reader at all");
        }
    }

    // MARK: - Tone rendering policy

    /// <summary>
    /// One meaning per color, pinned: red only for the live microphone, blue for working and
    /// info, orange for anything that needs the person. Failure wears orange, not red - every
    /// failure here keeps the work recoverable.
    /// </summary>
    [Fact]
    public void Scenario014()
    {
        Assert.Null(OverlayTone.Idle.ColorRole);
        Assert.Equal(StatusColorRole.Recording, OverlayTone.Recording.ColorRole);
        Assert.Equal(StatusColorRole.Processing, OverlayTone.Working.ColorRole);
        Assert.Equal(StatusColorRole.Processing, OverlayTone.Info.ColorRole);
        Assert.Equal(StatusColorRole.Attention, OverlayTone.Warning.ColorRole);
        Assert.Equal(StatusColorRole.Attention, OverlayTone.Failure.ColorRole);
    }

    /// <summary>
    /// Failure and warning share one icon: both mean "look here, your work is preserved". The old
    /// cross read as a destructive dead end.
    /// </summary>
    [Fact]
    public void Scenario015()
    {
        Assert.Equal(OverlayIcon.Warning, OverlayTone.Failure.Icon);
        Assert.Equal(OverlayIcon.Warning, OverlayTone.Warning.Icon);
        Assert.Equal(OverlayIcon.Info, OverlayTone.Info.Icon);
        Assert.Equal(OverlayIcon.Microphone, OverlayTone.Recording.Icon);
        Assert.Equal(OverlayIcon.Waveform, OverlayTone.Working.Icon);
        Assert.Equal(OverlayIcon.Done, OverlayTone.Idle.Icon);
    }

    /// <summary>
    /// While the engine is being read back into memory, the panel says so instead of claiming to
    /// transcribe.
    ///
    /// Residency hands the weights back to the system when the machine is short of memory, and the
    /// keypress that follows starts a full reload. Until it lands there is nothing to recognize
    /// with - and "Transcribing…" over a model still coming off disk is a lie that can hold the
    /// screen for a minute and a half. That minute and a half is when a person decides the product
    /// is broken.
    /// </summary>
    [Fact]
    public void ALoadingEngineIsNarratedInsteadOfClaimingToTranscribe()
    {
        EnginePreparationState loading = EnginePreparationState.Make(
            EnginePreparationState.Phase.LoadingRecognizer,
            TimeSpan.FromSeconds(34));

        OverlayContent content = OverlayContent.Make(
            DictationState.Transcribing,
            notice: null,
            elapsed: TimeSpan.FromSeconds(3),
            enginePreparation: loading);

        Assert.Equal(loading.Title, content.Title);
        Assert.Contains("34", content.Title, StringComparison.Ordinal);
        // The one thing a person waiting on a reload needs to know.
        Assert.Equal("Your words are recorded and waiting.", content.Subtitle);
        Assert.Equal(OverlayTone.Working, content.Tone);
        Assert.Contains(loading.Title, content.AccessibilityLabel, StringComparison.Ordinal);
        // Silent: the title carries a seconds counter, and announcing it would interrupt the
        // screen reader once per second for as long as the load lasts.
        Assert.Null(content.Announcement);
    }

    /// <summary>
    /// A ready or idle engine changes nothing: the ordinary panel is the ordinary panel.
    /// </summary>
    [Fact]
    public void AnEngineThatIsNotLoadingLeavesThePanelAlone()
    {
        foreach (EnginePreparationState.Phase phase in
            new[] { EnginePreparationState.Phase.Idle, EnginePreparationState.Phase.Ready })
        {
            OverlayContent content = OverlayContent.Make(
                DictationState.Transcribing,
                notice: null,
                elapsed: TimeSpan.Zero,
                enginePreparation: EnginePreparationState.Make(phase, TimeSpan.Zero));

            Assert.Equal("Transcribing…", content.Title);
        }

        Assert.Equal(
            "Transcribing…",
            OverlayContent.Make(DictationState.Transcribing, null, TimeSpan.Zero).Title);
    }

    /// <summary>
    /// A reload while the microphone is still open changes nothing either.
    ///
    /// The warm-up rides under the person's voice on purpose, and the panel's job during those
    /// seconds is to prove the microphone is live. Replacing "Listening" with a loading line would
    /// take away the only thing that says it is safe to speak.
    /// </summary>
    [Fact]
    public void ALoadingEngineNeverInterruptsTheRecordingPanel()
    {
        EnginePreparationState loading = EnginePreparationState.Make(
            EnginePreparationState.Phase.LoadingRecognizer,
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            "Listening",
            OverlayContent.Make(DictationState.Listening, null, TimeSpan.Zero, loading).Title);
        Assert.Equal(
            "Turning on the microphone…",
            OverlayContent.Make(DictationState.Preparing, null, TimeSpan.Zero, loading).Title);
    }

    /// <summary>A message still outranks everything, a loading engine included.</summary>
    [Fact]
    public void AMessageStillOutranksALoadingEngine()
    {
        var notice = new DictationNotice(DictationNoticeKind.Failure, "Something to read.");
        OverlayContent content = OverlayContent.Make(
            DictationState.Transcribing,
            notice,
            TimeSpan.Zero,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(9)));

        Assert.Equal("Something to read.", content.Title);
    }
}
