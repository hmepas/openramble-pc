namespace DictationCore.Tests;

/// <summary>
/// One path owns the end of a take. Whoever claimed it - the stop, a watchdog, a rescue, a
/// cancellation or an interruption - is the only one that may speak for the take and the only one
/// that may clean it up; every other continuation that wakes up later only lets go of what it holds.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class TakeEndingOwnershipTests
{
    private static async Task Settle(int iterations = 15)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A panel whose "Transcribing" and whose dismissal can be held, so a continuation can be
    /// parked exactly where the races below need it.
    /// </summary>
    private sealed class HeldOverlay : IOverlayPresenting
    {
        private readonly Lock gate = new();
        private readonly List<DictationState> presented = [];
        private readonly List<DictationNotice> notices = [];
        private int dismissCount;

        public Gate? TranscribingGate { get; set; }

        public Gate? DismissGate { get; set; }

        public Gate? NoticeGate { get; set; }

        public bool ThrowsOnNotice { get; set; }

        public IReadOnlyList<DictationState> Presented { get { lock (gate) { return presented.ToArray(); } } }

        public IReadOnlyList<DictationNotice> Notices { get { lock (gate) { return notices.ToArray(); } } }

        public int DismissCount { get { lock (gate) { return dismissCount; } } }

        public async Task Present(DictationState state, TimeSpan elapsed)
        {
            lock (gate) { presented.Add(state); }
            if (state == DictationState.Transcribing && TranscribingGate is { } held) { await held.Pass(); }
        }

        public async Task Dismiss()
        {
            lock (gate) { dismissCount += 1; }
            if (DismissGate is { } held) { await held.Pass(); }
        }

        public async Task PresentNotice(DictationNotice notice)
        {
            lock (gate) { notices.Add(notice); }
            if (ThrowsOnNotice) { throw new InvalidOperationException("the panel is gone"); }
            if (NoticeGate is { } held) { await held.Pass(); }
        }
    }

    private static DictationController MakeController(TrackedCapture capture, HeldOverlay overlay, CountingSounds sounds) =>
        new(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.01))),
            inserter: new RecordingInserter(),
            overlay: overlay,
            sounds: sounds);

    /// <summary>
    /// Escape during the stop's first await: the abort closes the microphone before the stop gets
    /// to it, and the stop's "not recording" is the cancellation's doing, not a capture failure.
    /// </summary>
    [Fact]
    public async Task TestEscapeDuringTheStopDoesNotReportTheAbortAsACaptureFailure()
    {
        var capture = new TrackedCapture();
        var overlay = new HeldOverlay();
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        var transcribing = new Gate();
        var dismissing = new Gate();
        overlay.TranscribingGate = transcribing;
        overlay.DismissGate = dismissing;

        controller.Stop();
        await Settle();
        controller.Cancel();
        await Settle();
        Assert.False(capture.IsRecording);

        // The stop resumes into a microphone the abort already closed.
        transcribing.Open();
        await Settle();
        dismissing.Open();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Empty(overlay.Notices);
        Assert.Equal(0, sounds.AttentionPlays);
        Assert.Equal(1, overlay.DismissCount);
    }

    /// <summary>
    /// Escape, then the key comes up: the release belongs to a take the person already ended and
    /// must not start a finalization of it.
    /// </summary>
    [Fact]
    public async Task TestKeyUpAfterEscapeDoesNotFinalizeTheCancelledTake()
    {
        var capture = new TrackedCapture();
        var overlay = new HeldOverlay();
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        var transcribing = new Gate();
        var dismissing = new Gate();
        overlay.TranscribingGate = transcribing;
        overlay.DismissGate = dismissing;

        controller.Cancel();
        controller.Stop();
        await Settle();
        transcribing.Open();
        await Settle();
        dismissing.Open();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.DoesNotContain(DictationState.Transcribing, overlay.Presented);
        Assert.Empty(overlay.Notices);
        Assert.Equal(0, sounds.AttentionPlays);
        Assert.False(capture.IsRecording);
    }

    /// <summary>
    /// Escape while the microphone is still opening: the cancellation cleans the take up, and the
    /// start that returns afterwards only closes its microphone. A second cleanup would dismiss the
    /// panel a second time - by then, possibly the next take's.
    /// </summary>
    [Fact]
    public async Task TestEscapeDuringPreparingCleansTheTakeUpExactlyOnce()
    {
        var capture = new TrackedCapture();
        var starting = new Gate();
        capture.SetStartGate(starting);
        var overlay = new HeldOverlay();
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Preparing, controller.State);

        var dismissing = new Gate();
        overlay.DismissGate = dismissing;
        controller.Cancel();
        await Settle();

        // The start returns while the cancellation's cleanup is still dismissing the panel.
        starting.Open();
        await Settle();
        dismissing.Open();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(1, overlay.DismissCount);
        Assert.False(capture.IsRecording);
        Assert.Empty(overlay.Notices);
    }

    /// <summary>
    /// The same double cleanup through an interruption: the notice is the interruption's, said
    /// once, and the late start cleans nothing up.
    /// </summary>
    [Fact]
    public async Task TestInterruptionDuringPreparingCleansTheTakeUpExactlyOnce()
    {
        var capture = new TrackedCapture();
        var starting = new Gate();
        capture.SetStartGate(starting);
        var overlay = new HeldOverlay();
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        var dismissing = new Gate();
        overlay.DismissGate = dismissing;
        controller.Interrupt("The disk is full.");
        await Settle();
        starting.Open();
        await Settle();
        dismissing.Open();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(1, overlay.DismissCount);
        Assert.Single(overlay.Notices);
        Assert.Equal(1, sounds.AttentionPlays);
        Assert.False(capture.IsRecording);
    }

    /// <summary>
    /// An interruption whose notice never finishes presenting: the microphone is closed before
    /// the notice is said, not after, so a stuck panel cannot keep it open.
    /// </summary>
    [Fact]
    public async Task TestInterruptionWhoseNoticeHangsStillClosesTheMicrophone()
    {
        var capture = new TrackedCapture();
        var overlay = new HeldOverlay();
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        var noticing = new Gate();
        overlay.NoticeGate = noticing;
        controller.Interrupt("The disk is full.");
        await Settle();
        Assert.False(capture.IsRecording);

        noticing.Open();
        await Settle();
        Assert.Equal(DictationState.Idle, controller.State);
    }

    /// <summary>An interruption whose notice throws still ends its take.</summary>
    [Fact]
    public async Task TestInterruptionWhoseNoticeThrowsStillEndsTheTake()
    {
        var capture = new TrackedCapture();
        var overlay = new HeldOverlay { ThrowsOnNotice = true };
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Interrupt("The disk is full.");
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.False(capture.IsRecording);
        Assert.Equal(1, overlay.DismissCount);
    }

    /// <summary>A rescue whose notice throws still ends its take.</summary>
    [Fact]
    public async Task TestRescueWhoseNoticeThrowsStillEndsTheTake()
    {
        var capture = new TrackedCapture();
        var overlay = new HeldOverlay { ThrowsOnNotice = true };
        var sounds = new CountingSounds();
        var controller = MakeController(capture, overlay, sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.PreserveActiveRecording("The microphone was disconnected.");
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.False(capture.IsRecording);
        Assert.Equal(1, overlay.DismissCount);
    }

    /// <summary>
    /// A microphone whose stop waits for a gate and then fails the way a broken frame queue
    /// does: the file is closed and left on disk, and the failure names it.
    /// </summary>
    private sealed class BrokenStopCapture : IAudioCapturing
    {
        public BrokenStopCapture(string file) { File = file; }

        public string File { get; }

        public Gate StopGate { get; } = new();

        public Task<string> StartRecording()
        {
            System.IO.File.WriteAllText(File, "RIFF");
            return Task.FromResult(File);
        }

        public async Task<(string Url, TimeSpan Duration)> StopRecording()
        {
            await StopGate.Pass();
            throw new AudioCaptureError.StopFailed(File, new InvalidOperationException("the queue broke"));
        }

        // The stop already detached the take: an abort finds nothing to delete.
        public Task AbortRecording() => Task.CompletedTask;
    }

    /// <summary>
    /// Escape while a stop is closing the file, and the stop then fails: a cancelled dictation is
    /// deleted all the same, rather than left for the next launch to recover.
    /// </summary>
    [Fact]
    public async Task TestEscapeDuringAStopThatFailsStillDeletesTheRecording()
    {
        string file = Path.Combine(Path.GetTempPath(), $"broken-stop-{Guid.NewGuid():N}.wav");
        var capture = new BrokenStopCapture(file);
        var overlay = new HeldOverlay();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult("words", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.01))),
            inserter: new RecordingInserter(),
            overlay: overlay,
            sounds: new CountingSounds());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();
        controller.Cancel();
        await Settle();
        capture.StopGate.Open();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.False(File.Exists(file), "a cancelled dictation is deleted");
        Assert.Empty(overlay.Notices);
    }

    /// <summary>The same through a rescue: Escape during its stop deletes what the stop left.</summary>
    [Fact]
    public async Task TestEscapeDuringARescueWhoseStopFailsStillDeletesTheRecording()
    {
        string file = Path.Combine(Path.GetTempPath(), $"broken-rescue-{Guid.NewGuid():N}.wav");
        var capture = new BrokenStopCapture(file);
        var overlay = new HeldOverlay();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult("words", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.01))),
            inserter: new RecordingInserter(),
            overlay: overlay,
            sounds: new CountingSounds());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.PreserveActiveRecording("The microphone was disconnected.");
        await Settle();
        controller.Cancel();
        await Settle();
        capture.StopGate.Open();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.False(File.Exists(file), "a cancelled dictation is deleted");
        Assert.Empty(overlay.Notices);
    }
}
