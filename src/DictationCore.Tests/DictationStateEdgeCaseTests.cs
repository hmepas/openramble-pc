namespace DictationCore.Tests;

/// <summary>
/// Addition to the main policy checks: states which the usual scenario
/// does not reach, but in which the application ends up after quick presses.
/// </summary>
public sealed class DictationStateEdgeCaseTests
{
    // MARK: - Busy

    [Fact]
    public void TestEverySessionStateCountsAsBusy()
    {
        // Busyness protects against a second session over the first:
        // it is enough to forget one state to get two recordings at once
        // and two texts inserted mixed.
        Assert.False(DictationState.Idle.IsBusy);
        foreach (var state in new[]
        {
            DictationState.Preparing,
            DictationState.Listening,
            DictationState.Transcribing,
            DictationState.Inserting,
        })
        {
            Assert.True(state.IsBusy);
        }
    }

    // MARK: - Start

    [Fact]
    public void TestStartIsRefusedFromEveryNonIdleState()
    {
        // The hotkey is pressed faster than recognition happens. Each
        // such press should be rejected, and not start a second recording.
        foreach (var state in new[]
        {
            DictationState.Preparing,
            DictationState.Listening,
            DictationState.Transcribing,
            DictationState.Inserting,
        })
        {
            Assert.False(DictationStopPolicy.CanStart(state: state, isEnabled: true, isModelReady: true));
        }
    }

    [Fact]
    public void TestStartNeedsBothPermissionAndModel()
    {
        // Both reasons for failure are independent, and neither should override the other.
        Assert.False(DictationStopPolicy.CanStart(state: DictationState.Idle, isEnabled: false, isModelReady: false));
        Assert.True(DictationStopPolicy.CanStart(state: DictationState.Idle, isEnabled: true, isModelReady: true));
    }

    // MARK: - Speakerphone

    [Fact]
    public void TestHandsFreeIgnoresReleaseEvenWithoutASession()
    {
        // The speakerphone flag is checked before the state, so releasing the
        // key in hands-free mode does not mean anything at all - including
        // the case when there is no session.
        foreach (var state in new[]
        {
            DictationState.Idle,
            DictationState.Preparing,
            DictationState.Listening,
            DictationState.Transcribing,
            DictationState.Inserting,
        })
        {
            Assert.Equal(
                DeferredStopDecision.Ignore,
                DictationStopPolicy.DecideStop(state: state, isHandsFree: true));
        }
    }

    // MARK: - Record too short

    [Fact]
    public void TestShortPressBoundaryIsExact()
    {
        // The border runs exactly along the declared minimum: a little shorter -
        // a person has changed their mind, just that much - it is already speech.
        Assert.False(DictationDurationPolicy.IsWorthTranscribing(TimeSpan.Zero));
        Assert.False(
            DictationDurationPolicy.IsWorthTranscribing(
                DictationDurationPolicy.Minimum - TimeSpan.FromSeconds(0.01)));
        Assert.True(DictationDurationPolicy.IsWorthTranscribing(DictationDurationPolicy.Minimum));
    }

    [Fact]
    public void TestNegativeDurationIsNotWorthTranscribing()
    {
        // A negative duration should not come, but if it does because
        // the clock changed, there is nothing to recognize in it.
        Assert.False(DictationDurationPolicy.IsWorthTranscribing(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void TestEmptyRecordingAfterALongHoldIsAMicrophoneFault()
    {
        // The same empty recording, two completely different stories. A person
        // touched a key - they changed their mind, and there is nothing to tell them.
        // A person held the key for twelve seconds and spoke - and the recording
        // is empty: the microphone is muted, dead or taken by another
        // application. Being silent here means losing an entire paragraph without
        // a single word of explanation.
        Assert.Equal(
            DictationDurationPolicy.ShortRecordingOutcome.ReportSilentInput,
            DictationDurationPolicy.OutcomeForShortRecording(TimeSpan.FromSeconds(12)));
        Assert.Equal(
            DictationDurationPolicy.ShortRecordingOutcome.ReportSilentInput,
            DictationDurationPolicy.OutcomeForShortRecording(DictationDurationPolicy.MinimumHoldForSilentInput));
        Assert.Equal(
            DictationDurationPolicy.ShortRecordingOutcome.DropSilently,
            DictationDurationPolicy.OutcomeForShortRecording(TimeSpan.FromSeconds(0.2)));
        Assert.Equal(
            DictationDurationPolicy.ShortRecordingOutcome.DropSilently,
            DictationDurationPolicy.OutcomeForShortRecording(TimeSpan.Zero));
    }

    // MARK: - Continued finalization

    [Fact]
    public void TestFinalizationStopsWhenBothCancellationSignalsFire()
    {
        Assert.False(
            DictationFinalizationPolicy.ShouldContinue(
                state: DictationState.Inserting,
                cancellationRequested: true,
                taskCancelled: true));
    }

    [Fact]
    public void TestFinalizationContinuesWhileInserting()
    {
        // Insertion is the last state from which completion is still
        // meaningful: the text is already ready and is waiting to be sent to someone else's application.
        Assert.True(
            DictationFinalizationPolicy.ShouldContinue(
                state: DictationState.Inserting,
                cancellationRequested: false,
                taskCancelled: false));
    }

    // MARK: - Nothing was said

    /// <summary>
    /// Punctuation on its own is what this engine hears in silence, and it is not a dictation.
    /// </summary>
    /// <remarks>
    /// The border is a single letter or a single digit anywhere in the text. The counting unit is
    /// the Unicode scalar and not the UTF-16 code unit, which is why a letter from outside the
    /// Basic Multilingual Plane counts: neither half of its surrogate pair is a letter alone.
    /// </remarks>
    [Theory]
    // Nothing a voice put there.
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("...", false)]
    [InlineData("\u2026", false)]
    [InlineData(",", false)]
    [InlineData("\u2026\u2026\u2026\u2026\u2026", false)]
    [InlineData(" , \u2026 . ", false)]
    [InlineData("-\u2014-", false)]
    // A voice was there.
    [InlineData("3", true)]
    [InlineData("3.14", true)]
    [InlineData("\u0434\u0430!", true)]
    [InlineData("ok", true)]
    // Outside the Basic Multilingual Plane: mathematical bold capital A, one letter in two
    // UTF-16 units. Counting code units, neither of them is a letter and the whole dictation
    // would have been thrown away.
    [InlineData("\U0001D400", true)]
    public void TestOnlyLettersAndDigitsMakeATranscriptWorthInserting(string text, bool carries) =>
        Assert.Equal(carries, RecognizedTextPolicy.CarriesWords(text));
}
