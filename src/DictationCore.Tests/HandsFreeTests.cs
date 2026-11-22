namespace DictationCore.Tests;

/// <summary>
/// Mode without holding down a key.
///
/// It used to be unreachable: the double tap came when the session was already
/// running, and the attempt to start a new one did not pass the free-state
/// check. The mode was promised in the settings, but could not be switched on.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class HandsFreeTests
{
    private readonly FakeCapture capture = new();
    private readonly FakeInserter inserter = new();

    private DictationController MakeController() =>
        new(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "текст",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());

    private static async Task Settle(int iterations = 12)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TestDoubleTapDuringSessionSwitchesToHandsFree()
    {
        var controller = MakeController();

        // This is how it actually happens: the first tap starts the session, and
        // the double tap arrives on top of it.
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        controller.PromoteToHandsFree();

        // The mode must switch on.
        Assert.True(controller.IsHandsFreeActive);

        // Now releasing the key means nothing - the recording goes on.
        controller.Stop();
        await Settle(4);
        // The recording must go on after the release.
        Assert.Equal(DictationState.Listening, controller.State);

        controller.StopHandsFree();
        await Settle();

        // The second press finishes the dictation.
        Assert.Single(inserter.InsertedTexts);
    }

    [Fact]
    public async Task TestDoubleTapWhilePreparingAlsoWorks()
    {
        // A quick double tap arrives before the microphone comes up.
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);

        controller.PromoteToHandsFree();
        await Settle();

        Assert.True(controller.IsHandsFreeActive);
        Assert.Equal(DictationState.Listening, controller.State);
    }

    [Fact]
    public async Task TestPromotionClearsPendingRelease()
    {
        // The release belonged to the previous gesture: in the new mode the key
        // is supposed to be released, so it must not break the recording.
        //
        // The microphone opens only after both gestures, the order the Swift original gets for
        // free from its main actor. Without the gate a pool thread may open it between the
        // release and the promotion, and the test would measure the scheduler.
        var opening = new Gate();
        var gated = new TrackedCapture();
        gated.SetStartGate(opening);
        var controller = new DictationController(
            capture: gated,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "текст",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        controller.Stop();
        controller.PromoteToHandsFree();
        opening.Open();
        await Settle();

        // The recording must not break off.
        Assert.Equal(DictationState.Listening, controller.State);
        Assert.True(controller.IsHandsFreeActive);
    }

    [Fact]
    public void TestPromotionIsIgnoredWhenNothingIsRunning()
    {
        var controller = MakeController();

        controller.PromoteToHandsFree();

        // Without a session there is nothing to switch on.
        Assert.False(controller.IsHandsFreeActive);
    }

    [Fact]
    public async Task TestModeResetsAfterSessionEnds()
    {
        var controller = MakeController();
        controller.Begin(handsFree: true, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.True(controller.IsHandsFreeActive);

        controller.StopHandsFree();
        await Settle();

        // The next dictation starts in the ordinary mode, not in an inherited one.
        Assert.False(controller.IsHandsFreeActive);
    }
}
