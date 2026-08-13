namespace DictationCore.Tests;

/// <summary>
/// These rules look like a trifle, but it is on them that dictations in production break down:
/// a lost key release leaves the recording enabled, and an extra
/// finalization inserts the text twice.
/// </summary>
public sealed class DictationStatePolicyTests
{
    // MARK: - Release the key

    [Fact]
    public void TestReleaseDuringPreparingIsRemembered()
    {
        // The most common gesture: a short phrase, the key was released earlier
        // than the sound engine came up. Letting go cannot be lost.
        var decision = DictationStopPolicy.DecideStop(state: DictationState.Preparing, isHandsFree: false);

        Assert.Equal(DeferredStopDecision.DeferUntilListening, decision);
    }

    [Fact]
    public void TestReleaseWhileListeningStopsImmediately()
    {
        Assert.Equal(
            DeferredStopDecision.StopNow,
            DictationStopPolicy.DecideStop(state: DictationState.Listening, isHandsFree: false));
    }

    [Fact]
    public void TestHandsFreeIgnoresRelease()
    {
        // In hands-free mode, recording is stopped with a second press.
        // If you react to release, the dictation will end immediately after the start.
        foreach (var state in new[] { DictationState.Preparing, DictationState.Listening })
        {
            Assert.Equal(
                DeferredStopDecision.Ignore,
                DictationStopPolicy.DecideStop(state: state, isHandsFree: true));
        }
    }

    [Fact]
    public void TestReleaseDuringFinalizationIsIgnored()
    {
        // Otherwise, finalization will run a second time and the text will be inserted twice.
        foreach (var state in new[] { DictationState.Transcribing, DictationState.Inserting })
        {
            Assert.Equal(
                DeferredStopDecision.Ignore,
                DictationStopPolicy.DecideStop(state: state, isHandsFree: false));
        }
    }

    [Fact]
    public void TestReleaseWithoutSessionDoesNothing()
    {
        Assert.Equal(
            DeferredStopDecision.NoSession,
            DictationStopPolicy.DecideStop(state: DictationState.Idle, isHandsFree: false));
    }

    // MARK: - Start

    [Fact]
    public void TestStartRequiresIdleEnabledAndModel()
    {
        Assert.True(DictationStopPolicy.CanStart(state: DictationState.Idle, isEnabled: true, isModelReady: true));

        // Busy session - we do not start a new one on top.
        Assert.False(DictationStopPolicy.CanStart(state: DictationState.Listening, isEnabled: true, isModelReady: true));
        // Without a model there is nothing to recognize: better not to start than to record and fail.
        Assert.False(DictationStopPolicy.CanStart(state: DictationState.Idle, isEnabled: true, isModelReady: false));
        Assert.False(DictationStopPolicy.CanStart(state: DictationState.Idle, isEnabled: false, isModelReady: true));
    }

    // MARK: - Cancel

    [Fact]
    public void TestCancelIsPossibleUntilInsertionStarts()
    {
        Assert.True(DictationStopPolicy.CanCancel(DictationState.Preparing));
        Assert.True(DictationStopPolicy.CanCancel(DictationState.Listening));
        Assert.True(DictationStopPolicy.CanCancel(DictationState.Transcribing));

        // The insertion has already gone to someone else's application - there is nothing to recall.
        Assert.False(DictationStopPolicy.CanCancel(DictationState.Inserting));
        Assert.False(DictationStopPolicy.CanCancel(DictationState.Idle));
    }

    // MARK: - Continued finalization

    [Fact]
    public void TestFinalizationStopsOnCancellation()
    {
        Assert.False(
            DictationFinalizationPolicy.ShouldContinue(
                state: DictationState.Transcribing,
                cancellationRequested: true,
                taskCancelled: false),
            "after a cancellation request the session must not reach insertion");
        Assert.False(
            DictationFinalizationPolicy.ShouldContinue(
                state: DictationState.Transcribing,
                cancellationRequested: false,
                taskCancelled: true));
    }

    [Fact]
    public void TestFinalizationContinuesInNormalFlow()
    {
        Assert.True(
            DictationFinalizationPolicy.ShouldContinue(
                state: DictationState.Transcribing,
                cancellationRequested: false,
                taskCancelled: false));
    }

    [Fact]
    public void TestFinalizationRefusesFromWrongState()
    {
        // If the state has already been reset to idle, then the session has been terminated -
        // it is too late to insert text.
        foreach (var state in new[] { DictationState.Idle, DictationState.Preparing, DictationState.Listening })
        {
            Assert.False(
                DictationFinalizationPolicy.ShouldContinue(
                    state: state,
                    cancellationRequested: false,
                    taskCancelled: false));
        }
    }

    // MARK: - Microphone

    [Fact]
    public void TestMicrophoneIsOnlyLiveWhileListening()
    {
        // The "recording light turns off when we are not listening" promise holds on
        // exactly this: in all other states the engine is turned off.
        Assert.True(DictationState.Listening.IsCapturing);
        foreach (var state in new[]
        {
            DictationState.Idle,
            DictationState.Preparing,
            DictationState.Transcribing,
            DictationState.Inserting,
        })
        {
            Assert.False(state.IsCapturing);
        }
    }
}
