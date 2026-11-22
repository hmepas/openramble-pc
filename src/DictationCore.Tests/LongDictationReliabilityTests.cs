namespace DictationCore.Tests;

/// <remarks>
/// Ported from upstream 0.31.2. Not ported, with the reason:
/// <list type="bullet">
/// <item><description><c>testHealthyWAVPublicationPastTwoSecondsStillInserts</c> - the PC captures
/// close the WAV inside <c>StopRecording</c>; there is no separate "file readable" milestone to
/// wait on, so the stop test below covers the whole stretch.</description></item>
/// <item><description><c>testReportsTheActualFailedStageWithoutAnErrorDescription</c> - the
/// stage-failure journal (<c>onStageFailure</c>) belongs to upstream's error-report export, which
/// this port does not have.</description></item>
/// </list>
/// </remarks>
[Collection(DictationControllerCollection.Name)]
public sealed class LongDictationReliabilityTests
{
    /// <summary>
    /// A stop that takes longer than upstream's old half-second budget on a busy machine is
    /// still a healthy stop: the take is recognized and inserted, and nothing says it failed.
    /// </summary>
    [Fact]
    public async Task TestHealthyFreezePastHalfASecondStillInserts()
    {
        var capture = new Capture(stop: TimeSpan.FromMilliseconds(800));
        var inserter = new FakeInserter();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "complete take",
                audioDuration: TimeSpan.FromSeconds(298),
                processingDuration: TimeSpan.FromSeconds(0.01))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await WaitUntil(() => controller.State == DictationState.Listening);
        controller.Stop();
        await WaitUntil(() => controller.State == DictationState.Idle);

        Assert.Equal(["Complete take"], inserter.InsertedTexts);
        Assert.True(
            !notices.Any(notice => notice.Kind == DictationNoticeKind.Failure),
            "healthy finalization must not be a failure");
    }

    [Fact]
    public async Task TestCancelSkipsQueuedSegmentsAndDiscardsLateResult()
    {
        var engine = new GateEngine();
        var capture = new Capture(segments: 3);
        var inserter = new FakeInserter();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) =>
            {
                Assert.Fail("cancelled recording must not fall back");
                throw new OperationCanceledException();
            },
            transcribeSamples: (_, _) => engine.Run(),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await WaitUntil(() => controller.State == DictationState.Listening && engine.Calls == 1);
        controller.Cancel();
        await WaitUntil(() => controller.State == DictationState.Idle);
        engine.Open();
        await Task.Delay(150);

        Assert.True(engine.Calls == 1, "Escape must not leave the old segment queue running");
        Assert.Empty(inserter.InsertedTexts);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await WaitUntil(() => controller.State == DictationState.Listening);
        controller.Stop();
        await WaitUntil(() => controller.State == DictationState.Idle);
        await WaitUntil(() => inserter.InsertedTexts.Count == 1);

        Assert.True(inserter.InsertedTexts.Count == 1, "the next dictation still completes");
        // Every call answers with its own words, so the cancelled take's late result would show.
        Assert.DoesNotContain("call one", inserter.InsertedTexts[0], StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (!predicate() && DateTime.UtcNow < end)
        {
            await Task.Delay(5);
        }

        Assert.True(predicate());
    }

    /// <summary>
    /// The engine: the first call waits for the test, every later one answers at once - each with
    /// words of its own, so a result that leaks into another take can be told apart.
    /// </summary>
    private sealed class GateEngine
    {
        private readonly Gate gate = new();
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public async Task<ASRResult> Run()
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) { await gate.Pass(); }

            return new ASRResult(call == 1 ? "call one" : $"call {call}", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(0.01));
        }

        public void Open() => gate.Open();
    }

    private sealed class Capture : IAudioCapturing
    {
        private readonly Lock gate = new();
        private readonly string url = Path.Combine(Path.GetTempPath(), $"long-stop-{Guid.NewGuid()}.wav");
        private readonly TimeSpan stop;
        private readonly int segments;
        private Action<float[]>? sink;

        public Capture(TimeSpan stop = default, int segments = 0)
        {
            this.stop = stop;
            this.segments = segments;
        }

        public Task SetSegmentSink(Action<float[]>? sink)
        {
            lock (gate) { this.sink = sink; }
            return Task.CompletedTask;
        }

        public Task<string> StartRecording()
        {
            File.WriteAllText(url, "test recording");
            Action<float[]>? deliver;
            lock (gate) { deliver = sink; }

            for (int index = 0; index < segments; index += 1)
            {
                deliver?.Invoke(Enumerable.Repeat(0.1f, 4 * 16_000).ToArray());
            }

            return Task.FromResult(url);
        }

        public async Task<(string Url, TimeSpan Duration)> StopRecording()
        {
            if (stop > TimeSpan.Zero) { await Task.Delay(stop); }

            return (url, TimeSpan.FromSeconds(segments > 0 ? 16 : 298));
        }

        public Task<float[]?> TakeBufferedSamples() => Task.FromResult(
            segments > 0 ? Enumerable.Repeat(0.1f, 16 * 16_000).ToArray() : null);

        public Task<int> ConsumedSampleCount() => Task.FromResult(segments * 4 * 16_000);

        public Task AbortRecording()
        {
            try
            {
                File.Delete(url);
            }
            catch (IOException)
            {
            }

            return Task.CompletedTask;
        }
    }
}
