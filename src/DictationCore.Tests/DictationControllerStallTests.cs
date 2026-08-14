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
        private string? lastFile;

        public FileCapture(string directory)
        {
            this.directory = directory;
        }

        public Task<string> StartRecording()
        {
            string url = Path.Combine(directory, $"take-{Guid.NewGuid()}.wav");
            File.WriteAllText(url, "sound");
            lock (gate) { lastFile = url; }
            return Task.FromResult(url);
        }

        public Task<(string Url, TimeSpan Duration)> StopRecording()
        {
            lock (gate)
            {
                if (lastFile is not { } url) { throw new AudioCaptureError.NotRecording(); }
                return Task.FromResult((url, TimeSpan.FromSeconds(3)));
            }
        }

        public Task AbortRecording() => Task.CompletedTask;
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

    /// <summary>
    /// A reload that wedges (prepare never returns) flows into the same
    /// recovery as a wedged recognition: bounded failure, recording kept,
    /// stall signalled - the person is never held hostage by a cold engine.
    /// </summary>
    [Fact]
    public async Task TestWedgedPrepareResolvesLikeAWedgedTranscription()
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
        Assert.Equal(1, stalls);
        Assert.NotEmpty(notices);
        DictationNotice notice = notices[^1];
        Assert.Equal(DictationNoticeKind.Failure, notice.Kind);
        // The words survive a wedged reload too.
        Assert.NotNull(notice.RecoveryAudio);
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
