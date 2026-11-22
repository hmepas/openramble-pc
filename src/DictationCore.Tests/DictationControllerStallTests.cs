using System.Diagnostics;

namespace DictationCore.Tests;

/// <summary>
/// A wedged engine - a recognition call that never returns - must resolve
/// into a bounded failure that keeps the recording, frees the session, and
/// tells the owner to recycle the engine. Observed in the wild: an inference
/// call stuck on a restarted system service held "Transcribing…" indefinitely,
/// and Escape was the only exit - at the price of the words.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class DictationControllerStallTests : IDisposable
{
    private readonly string root;
    private readonly string takes;
    private readonly string recovered;

    public DictationControllerStallTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"stall-tests-{Guid.NewGuid()}");
        takes = Path.Combine(root, "Takes");
        recovered = Path.Combine(root, "Recovered");
        Directory.CreateDirectory(takes);
        Directory.CreateDirectory(recovered);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileCapture : IAudioCapturing
    {
        private readonly Lock gate = new();
        private readonly string directory;
        private readonly TimeSpan freezeDelay;
        private string? lastFile;

        public FileCapture(string directory, TimeSpan freezeDelay = default)
        {
            this.directory = directory;
            this.freezeDelay = freezeDelay;
        }

        public Task<string> StartRecording()
        {
            string url = Path.Combine(directory, $"take-{Guid.NewGuid()}.wav");
            File.WriteAllText(url, "sound");
            lock (gate) { lastFile = url; }
            return Task.FromResult(url);
        }

        public async Task<(string Url, TimeSpan Duration)> StopRecording()
        {
            string url;
            lock (gate)
            {
                url = lastFile ?? throw new AudioCaptureError.NotRecording();
            }

            if (freezeDelay > TimeSpan.Zero) { await Task.Delay(freezeDelay); }

            return (url, TimeSpan.FromSeconds(3));
        }

        public Task AbortRecording() => Task.CompletedTask;
    }

    /// <summary>An inserter whose chosen system edge never returns.</summary>
    private sealed class WedgeInserter : ITextInserting
    {
        public enum Wedge { Insert, PressReturn }

        private readonly Lock gate = new();
        private readonly Wedge wedge;
        private readonly List<string> insertedTexts = [];

        public WedgeInserter(Wedge wedge) { this.wedge = wedge; }

        public IReadOnlyList<string> InsertedTexts { get { lock (gate) { return insertedTexts.ToArray(); } } }

        public async Task Insert(string text, TargetApplication? target)
        {
            if (wedge == Wedge.Insert) { await WedgeFixtures.SuspendForever<bool>(); }

            lock (gate) { insertedTexts.Add(text); }
        }

        public async Task PressReturn()
        {
            if (wedge == Wedge.PressReturn) { await WedgeFixtures.SuspendForever<bool>(); }
        }

        public TargetApplication? FrontmostApplication() => null;
    }

    /// <summary>
    /// A capture whose stop never returns.
    /// </summary>
    /// <remarks>
    /// The audio service dying under a live stream is the real shape of this: the stop is a call
    /// into it, and a call into something that died does not come back. It is the one stretch of
    /// the finalization that used to be covered by nothing at all, so a session could sit on
    /// "Transcribing…" with the microphone still open, no message, no sound, forever.
    /// </remarks>
    private sealed class WedgedStopCapture : IAudioCapturing
    {
        private int aborts;

        public int Aborts => Volatile.Read(ref aborts);

        public Task<string> StartRecording() => Task.FromResult("take.wav");

        public Task<(string Url, TimeSpan Duration)> StopRecording() =>
            WedgeFixtures.SuspendForever<(string, TimeSpan)>();

        public Task AbortRecording()
        {
            Interlocked.Increment(ref aborts);
            return Task.CompletedTask;
        }
    }

    private sealed class NullInserter : ITextInserting
    {
        public TargetApplication? FrontmostApplication() => null;

        public Task Insert(string text, TargetApplication? target) => Task.CompletedTask;

        public Task PressReturn() => Task.CompletedTask;
    }

    private sealed class NullOverlay : IOverlayPresenting
    {
        public Task Present(DictationState state, TimeSpan elapsed) => Task.CompletedTask;

        public Task PresentNotice(DictationNotice notice) => Task.CompletedTask;

        public Task Dismiss() => Task.CompletedTask;
    }

    private sealed class NullSounds : ISounding
    {
        public Task PlayAttention() => Task.CompletedTask;
    }

    private static async Task Settle(int iterations = 40)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    private static async Task WaitForIdle(DictationController controller, int limit = 200)
    {
        for (int i = 0; i < limit && controller.State != DictationState.Idle; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TestWedgedTranscriptionResolvesKeepsRecordingAndSignalsStall()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            // The premise: an engine call stuck on a dead service, deaf to
            // cancellation, never returning.
            transcribe: (_, _) => WedgeFixtures.SuspendForever<ASRResult>(),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            transcriptionDeadline: _ => TimeSpan.FromMilliseconds(80));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        controller.Stop();
        await WaitForIdle(controller);

        // A wedged engine must not hold the session.
        Assert.Equal(DictationState.Idle, controller.State);
        // The owner is told exactly once to recycle the engine.
        Assert.Equal(1, stalls);

        Assert.NotEmpty(notices);
        DictationNotice notice = notices[^1];
        Assert.Equal(DictationNoticeKind.Failure, notice.Kind);
        Assert.Contains("took too long", notice.Message, StringComparison.Ordinal);
        // The words must survive the stall.
        Assert.NotNull(notice.RecoveryAudio);
        // The take moves into safekeeping.
        Assert.Single(Directory.GetFiles(recovered, "*.wav"));
    }

    [Fact]
    public async Task TestWedgedInsertionReturnsIdleAndKeepsRecognizedTextInMemory()
    {
        var inserter = new WedgeInserter(WedgeInserter.Wedge.Insert);
        var controller = new DictationController(
            capture: new FileCapture(takes),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "text that must survive",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.01))),
            inserter: inserter,
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            insertionDeadline: TimeSpan.FromMilliseconds(50));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller, limit: 250);

        Assert.True(controller.State == DictationState.Idle, "a cancellation-deaf paste edge cannot own the next dictation");
        RecoveredDictation? recovered = controller.PendingRecovery;
        Assert.NotNull(recovered);
        Assert.Equal("Text that must survive", recovered.Text);
        Assert.NotEmpty(notices);
        DictationNotice notice = notices[^1];
        Assert.Equal(recovered.Text, notice.RecoverableText);
        Assert.Contains("couldn't be confirmed", notice.Message);
    }

    [Fact]
    public async Task TestWedgedReturnDoesNotHoldSessionAfterTextAlreadyLanded()
    {
        var inserter = new WedgeInserter(WedgeInserter.Wedge.PressReturn);
        var controller = new DictationController(
            capture: new FileCapture(takes),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "message. отправь",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.01))),
            inserter: inserter,
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            pipeline: _ => new TextPipeline(allowPressReturnCommand: true),
            returnDeadline: TimeSpan.FromMilliseconds(50));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller, limit: 250);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(["Message."], inserter.InsertedTexts);
        Assert.True(controller.PendingRecovery is null, "the text itself already landed");
        Assert.Contains(notices, notice => notice.Message.Contains("pressing Return failed"));
    }

    /// <summary>
    /// A reload that outlives the foreground budget is not mistaken for a wedged inference. The
    /// owner's own preparation retry owns recovery; the controller returns promptly and keeps
    /// the take without killing an otherwise healthy load.
    /// </summary>
    [Fact]
    public async Task TestWedgedPrepareReturnsPromptlyWithoutRecyclingHealthyLoad()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "never reached",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            prepareForTranscription: _ => WedgeFixtures.SuspendForever<bool>(),
            prepareDeadline: TimeSpan.FromMilliseconds(80));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(0, stalls);
        Assert.NotEmpty(notices);
        DictationNotice notice = notices[^1];
        Assert.Equal(DictationNoticeKind.Failure, notice.Kind);
        Assert.Contains("still getting ready", notice.Message);
        // The words survive a wedged reload too.
        Assert.NotNull(notice.RecoveryAudio);
    }

    /// <summary>
    /// The stop→text promise excludes a healthy reload: the inference deadline starts only
    /// after the reload finished, instead of inheriting a budget the reload already spent.
    /// </summary>
    [Fact]
    public async Task TestPrepareExtendsTheStopBudgetByItsActualDuration()
    {
        var capture = new FileCapture(takes, freezeDelay: TimeSpan.FromMilliseconds(40));
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => WedgeFixtures.SuspendForever<ASRResult>(),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            transcriptionDeadline: _ => TimeSpan.FromMilliseconds(80),
            prepareForTranscription: token => Task.Delay(70, token),
            prepareDeadline: TimeSpan.FromMilliseconds(200));
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        long released = Stopwatch.GetTimestamp();
        controller.Stop();
        await WaitForIdle(controller, limit: 400);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(released);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.True(stalls == 1, "the full inference budget still owns stall recovery");
        Assert.True(
            elapsed > TimeSpan.FromMilliseconds(150),
            "the inference deadline starts only after the reload finished");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), "stage ceilings still bound the Transcribing wait");
    }

    /// <summary>
    /// The product case behind wait-and-insert: a reload that outlives the old stop budget still
    /// delivers the words once it completes.
    /// </summary>
    [Fact]
    public async Task TestColdPrepareLongerThanStopBudgetStillInsertsText()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            transcriptionDeadline: _ => TimeSpan.FromMilliseconds(80),
            prepareForTranscription: token => Task.Delay(300, token),
            prepareDeadline: TimeSpan.FromSeconds(1));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        string? inserted = null;
        controller.OnTextInserted = text => inserted = text;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller, limit: 400);

        Assert.Equal(DictationState.Idle, controller.State);
        // The pipeline sentence-cases the raw engine text on the way in.
        Assert.Equal("Words", inserted);
        Assert.True(
            !notices.Any(notice => notice.Kind == DictationNoticeKind.Failure),
            $"a healthy reload is not a failure: {string.Join("; ", notices.Select(notice => notice.Message))}");
    }

    /// <summary>
    /// After a cold prepare the wedge containment still works: exactly one stall, one
    /// transcription deadline after the reload finished.
    /// </summary>
    [Fact]
    public async Task TestInferenceKeepsFullDeadlineAfterColdPrepare()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => WedgeFixtures.SuspendForever<ASRResult>(),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            transcriptionDeadline: _ => TimeSpan.FromMilliseconds(80),
            prepareForTranscription: token => Task.Delay(200, token),
            prepareDeadline: TimeSpan.FromSeconds(1));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller, limit: 400);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.True(stalls == 1, "the wedge is still contained after the reload wait");
        Assert.NotEmpty(notices);
        DictationNotice notice = notices[^1];
        Assert.Contains("took too long", notice.Message);
        Assert.NotNull(notice.RecoveryAudio);
    }

    /// <summary>Escape while waiting out a cold reload stays a quiet cancellation.</summary>
    [Fact]
    public async Task TestCancelDuringColdPrepareWaitStaysQuiet()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "never reached",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            prepareForTranscription: _ => WedgeFixtures.SuspendForever<bool>(),
            prepareDeadline: TimeSpan.FromSeconds(5));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle(10);
        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        await WaitForIdle(controller);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(0, stalls);
        Assert.True(
            !notices.Any(notice => notice.Kind == DictationNoticeKind.Failure),
            $"cancelling the wait is the person's choice, not a failure: {string.Join("; ", notices.Select(notice => notice.Message))}");
    }

    /// <summary>A healthy prepare adds nothing observable: recognition proceeds.</summary>
    [Fact]
    public async Task TestHealthyPrepareIsInvisible()
    {
        var capture = new FileCapture(takes);
        int prepared = 0;
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            prepareForTranscription: _ =>
            {
                Interlocked.Increment(ref prepared);
                return Task.CompletedTask;
            },
            prepareDeadline: TimeSpan.FromSeconds(5));
        bool inserted = false;
        controller.OnTextInserted = _ => inserted = true;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller);

        Assert.Equal(1, Volatile.Read(ref prepared));
        // Prepare must not disturb a healthy dictation.
        Assert.True(inserted);
    }

    /// <summary>
    /// A capture that never closes its take resolves too - and lets go of the microphone.
    ///
    /// Everything after this point in the finalization has a deadline; this stretch had none, and
    /// it is the one that runs first. A hang here is therefore the only remaining way to hold
    /// "Transcribing…" with no notice, no sound and nothing to press - which is exactly what the
    /// person reported before reaching for Task Manager.
    /// </summary>
    [Fact]
    public async Task TestWedgedCaptureStopResolvesAndReleasesTheMicrophone()
    {
        var capture = new WedgedStopCapture();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => throw new InvalidOperationException("never reached"),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            captureStopDeadline: TimeSpan.FromMilliseconds(80));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        controller.Stop();
        await WaitForIdle(controller);

        // The session is free and the panel has something to say.
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.NotEmpty(notices);
        DictationNotice notice = notices[^1];
        Assert.Equal(DictationNoticeKind.Failure, notice.Kind);
        Assert.Contains("didn't stop", notice.Message, StringComparison.Ordinal);
        // The words are gone, so the ear is used: this is exactly the loss the sound is for.
        Assert.True(notice.WordsDidNotLand);
        // The engine did nothing wrong - blaming it would recycle a healthy one.
        Assert.Equal(0, stalls);

        // And the microphone is asked to close, even though the stop that owned it never returned.
        for (int i = 0; i < 100 && capture.Aborts == 0; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }

        Assert.Equal(1, capture.Aborts);
    }

    /// <summary>
    /// testReportsTheActualFailedStageWithoutAnErrorDescription, for the stage this port bounds:
    /// a capture that never closes is reported as a timed-out capture freeze, with how long it ran.
    /// </summary>
    [Fact]
    public async Task TestReportsTheActualFailedStageWithoutAnErrorDescription()
    {
        var controller = new DictationController(
            capture: new WedgedStopCapture(),
            transcribe: (_, _) => throw new InvalidOperationException("never reached"),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            captureStopDeadline: TimeSpan.FromMilliseconds(40));
        List<(DictationFailureStage Stage, TimeSpan Elapsed, bool TimedOut)> failures = [];
        controller.OnStageFailure = (stage, elapsed, timedOut) => failures.Add((stage, elapsed, timedOut));

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller);

        var failure = Assert.Single(failures);
        Assert.Equal(DictationFailureStage.CaptureFreeze, failure.Stage);
        Assert.True(failure.TimedOut);
        Assert.True(failure.Elapsed >= TimeSpan.FromMilliseconds(30));
    }

    /// <summary>A healthy stop is untouched by the new budget: it is not even close to it.</summary>
    [Fact]
    public async Task TestAHealthyStopIsUnaffectedByTheCaptureBudget()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            captureStopDeadline: TimeSpan.FromSeconds(5));
        bool inserted = false;
        controller.OnTextInserted = _ => inserted = true;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForIdle(controller);

        Assert.True(inserted);
    }

    /// <summary>
    /// Escape during a wedged transcription stays a quiet cancellation:
    /// no stall signal, no failure notice, nothing kept.
    /// </summary>
    [Fact]
    public async Task TestCancellationDuringWedgeStaysQuietCancellation()
    {
        var capture = new FileCapture(takes);
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => WedgeFixtures.SuspendForever<ASRResult>(),
            inserter: new NullInserter(),
            overlay: new NullOverlay(),
            sounds: new NullSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered),
            transcriptionDeadline: _ => TimeSpan.FromSeconds(30));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;
        int stalls = 0;
        controller.OnTranscriptionStall = () => stalls += 1;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle(10);
        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        await WaitForIdle(controller);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(0, stalls);
        // Cancellation is the person's choice, not a failure.
        Assert.DoesNotContain(notices, notice => notice.Kind == DictationNoticeKind.Failure);
    }
}
