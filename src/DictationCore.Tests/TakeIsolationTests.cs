namespace DictationCore.Tests;

/// <summary>
/// One take's continuations act on that take and nothing else, and every way out of a take ends
/// in a cleanup.
///
/// The controller's awaits can resume after the person has pressed Escape or started the next
/// dictation; whatever they do then must neither touch the next take nor speak for the one that
/// was ended.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class TakeIsolationTests
{
    private static ASRResult Recognized(string text) =>
        new(text, audioDuration: TimeSpan.FromSeconds(2), processingDuration: TimeSpan.FromSeconds(0.01));

    private static async Task Settle(int iterations = 15)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    /// <summary>A capture whose post-stop reads can be held, like a busy capture actor.</summary>
    private sealed class HeldReadsCapture : IAudioCapturing
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), $"held-{Guid.NewGuid():N}.wav");

        public TaskCompletionSource ReadsReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadsReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> StartRecording() => Task.FromResult(file);

        public Task<(string Url, TimeSpan Duration)> StopRecording() => Task.FromResult((file, TimeSpan.FromSeconds(2)));

        public Task AbortRecording() => Task.CompletedTask;

        public async Task<TimeSpan?> StartupLatency()
        {
            ReadsReached.TrySetResult();
            await ReadsReleased.Task.ConfigureAwait(false);
            return TimeSpan.FromMilliseconds(100);
        }
    }

    /// <summary>Cleanup took everything away: what the pipeline produced is no words at all.</summary>
    private sealed class EverythingCleanedUp : ITextProcessing
    {
        public TextPipeline.RunResult Run(string recognized)
        {
            TextPipeline.RunResult real = new TextPipeline().Run(recognized);
            return new TextPipeline.RunResult(new TextPipeline.Output(string.Empty, null), real.Provenance);
        }
    }

    private sealed class ThrowingPipeline : ITextProcessing
    {
        public TextPipeline.RunResult Run(string recognized) => throw new InvalidOperationException("pipeline broke");
    }

    private sealed class KeepingRecovery : IRecordingRecoveryStoring
    {
        public int Preserved;

        public Task<string?> Preserve(string source)
        {
            Interlocked.Increment(ref Preserved);
            return Task.FromResult<string?>("/kept/take.wav");
        }
    }

    [Fact]
    public async Task TestEscapeDuringTheLastCaptureReadLeavesNoTraceOfTheTake()
    {
        var capture = new HeldReadsCapture();
        var overlay = new FakeOverlay();
        int kept = 0;
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(Recognized("words that cleanup removes")),
            inserter: new FakeInserter(),
            overlay: overlay,
            sounds: new FakeSounds(),
            pipeline: _ => new EverythingCleanedUp());
        controller.OnTakeFinished = (_, _, _) =>
        {
            Interlocked.Increment(ref kept);
            return true;
        };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await capture.ReadsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.Cancel();
        await Settle();
        capture.ReadsReleased.TrySetResult();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(0, Volatile.Read(ref kept));
        Assert.Empty(overlay.Notices);
    }

    [Fact]
    public async Task TestAPipelineThatThrowsKeepsTheTakeAndFreesTheSession()
    {
        var recovery = new KeepingRecovery();
        var overlay = new FakeOverlay();
        var controller = new DictationController(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(Recognized("recognized words")),
            inserter: new FakeInserter(),
            overlay: overlay,
            sounds: new FakeSounds(),
            recordingRecovery: recovery,
            pipeline: _ => new ThrowingPipeline());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(1, recovery.Preserved);
        DictationNotice notice = Assert.Single(overlay.Notices);
        Assert.Equal(DictationNoticeKind.Failure, notice.Kind);
        Assert.Equal("/kept/take.wav", notice.RecoveryAudio);
    }

    [Fact]
    public async Task TestASlowTargetQueryHoldsNeitherTheKeyDownNorTheMicrophone()
    {
        var target = new TargetApplication("org.example.Editor", 7, "Editor");
        using var queryMayAnswer = new ManualResetEventSlim();
        var capture = new FakeCapture();
        var inserter = new FakeInserter();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(Recognized("to the editor")),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            targetApplicationSnapshot: () =>
            {
                queryMayAnswer.Wait(TimeSpan.FromSeconds(5));
                return target;
            });

        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        TimeSpan keyDown = System.Diagnostics.Stopwatch.GetElapsedTime(began);
        await Settle();

        Assert.True(keyDown < TimeSpan.FromSeconds(1), $"the key-down waited {keyDown} for the window system");
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(DictationState.Listening, controller.State);

        controller.Stop();
        await Settle();
        Assert.Empty(inserter.InsertedTexts);

        queryMayAnswer.Set();
        await Settle();
        Assert.Equal(target, Assert.Single(inserter.Targets));
    }

    [Fact]
    public async Task TestATakeRecognizedFromItsFileSaysSoInItsPhases()
    {
        var reports = new List<DictationSpeedReport>();
        var controller = new DictationController(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(Recognized("from the file")),
            inserter: new FakeInserter(),
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            transcribeSamples: (_, _) => Task.FromResult(Recognized("from memory")));
        controller.OnSpeed = report => { lock (reports) { reports.Add(report); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        DictationSpeedReport speed = Assert.Single(reports);
        Assert.NotNull(speed.Phases);
        Assert.NotNull(speed.Phases.RecordingReadable);
    }

    [Fact]
    public async Task TestATakeRecognizedFromMemoryHasNoFileWait()
    {
        var reports = new List<DictationSpeedReport>();
        var capture = new FakeCapture();
        capture.SetBufferedSamples(Enumerable.Repeat(0.3f, 32_000).ToArray());
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(Recognized("from the file")),
            inserter: new FakeInserter(),
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            transcribeSamples: (_, _) => Task.FromResult(Recognized("from memory")));
        controller.OnSpeed = report => { lock (reports) { reports.Add(report); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        DictationSpeedReport speed = Assert.Single(reports);
        Assert.NotNull(speed.Phases);
        Assert.Null(speed.Phases.RecordingReadable);
    }
}
