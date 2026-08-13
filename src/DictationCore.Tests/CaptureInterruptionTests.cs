namespace DictationCore.Tests;

/// <summary>
/// A recording that ended on its own.
///
/// The disk fills up in the middle of a phrase, and this used to be known only
/// at the stop: the person went on speaking for five minutes into nowhere. Now
/// the dictation stops at once and names the reason.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class CaptureInterruptionTests
{
    private static DictationController MakeController(
        FakeCapture capture,
        FakeOverlay? overlay = null) =>
        new(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "текст",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new FakeInserter(),
            overlay: overlay ?? new FakeOverlay(),
            sounds: new FakeSounds());

    private static async Task Settle(int iterations = 15)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TestInterruptionStopsTheSessionAndExplainsWhy()
    {
        var capture = new FakeCapture();
        var controller = MakeController(capture);
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        controller.Interrupt(reason: "Кончилось место на диске.");
        await Settle();

        // The dictation must stop immediately.
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Single(notices);
        Assert.Equal(DictationNoticeKind.Failure, notices[0].Kind);
        Assert.Equal("Кончилось место на диске.", notices[0].Message);
    }

    [Fact]
    public async Task TestInterruptionAbortsRecordingInsteadOfFinishingIt()
    {
        // There is nowhere to append - the file must be dropped, not closed.
        var capture = new FakeCapture();
        var controller = MakeController(capture);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Interrupt(reason: "сбой записи");
        await Settle();

        // The recording is thrown away.
        Assert.Equal(1, capture.AbortCount);
        // And it is not finalized.
        Assert.Equal(0, capture.StopCount);
    }

    [Fact]
    public async Task TestInterruptionNeverInsertsText()
    {
        var capture = new FakeCapture();
        var inserter = new FakeInserter();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "текст",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Interrupt(reason: "сбой записи");
        await Settle();

        // There is nothing to insert from an interrupted recording.
        Assert.Empty(inserter.InsertedTexts);
    }

    [Fact]
    public async Task TestInterruptionIsIgnoredWhenNothingIsRecording()
    {
        // The failure message may arrive late, when the dictation has already
        // ended. An error cannot be shown retroactively.
        var capture = new FakeCapture();
        var controller = MakeController(capture);
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;

        controller.Interrupt(reason: "сбой записи");
        await Settle(4);

        Assert.Equal(DictationState.Idle, controller.State);
        // An empty complaint out of the blue is of no use to the user.
        Assert.Empty(notices);
    }

    [Fact]
    public async Task TestSessionCanStartAgainAfterInterruption()
    {
        var capture = new FakeCapture();
        var controller = MakeController(capture);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Interrupt(reason: "сбой записи");
        await Settle();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        // The place is free again - dictating goes on.
        Assert.Equal(DictationState.Listening, controller.State);
    }
}
