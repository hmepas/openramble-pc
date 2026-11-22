using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// Long recordings reach the engine in bounded pieces, cut at pauses like upstream's server.
/// </summary>
/// <remarks>
/// Not ported from upstream's <c>LongInputRoutingTests</c>:
/// <c>testLongPublicInputsStillAcceptBaseOnlyEnginesWithoutWordTimestamps</c> - upstream keeps a
/// one-call path for engines that do not implement its timed-batch protocol; this port has one
/// kind of engine, every long input is pieced, and a piece without word timings keeps its text
/// (see <see cref="TimedTranscriptJoiner"/>).
/// </remarks>
public sealed class LongInputRoutingTests : IDisposable
{
    private readonly string directory;

    public LongInputRoutingTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"long-pieces-{Guid.NewGuid():D}");
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task LongMemoryAndFileInputsReachTheEngineWholeInBoundedCalls()
    {
        foreach (int seconds in new[] { 240, 480 })
        {
            float[] samples = Speech(seconds);
            var engine = new PieceEngine();
            var transcriber = new LocalTranscriber(engine);
            await transcriber.Prepare("/unused");
            ASRResult memory = await transcriber.Transcribe((float[])samples.Clone());
            List<float[]> fromMemory = engine.Received;
            string path = WavFixture.WriteSamples(directory, samples, "long-pieces");
            ASRResult file = await transcriber.Transcribe(path);
            List<float[]> fromFile = engine.Received.Skip(fromMemory.Count).ToList();
            // Speech without a pause is cut into contiguous pieces: every sample once, in order,
            // nothing shared between pieces.
            Assert.Equal(samples, fromMemory.SelectMany(piece => piece));
            Assert.Equal(samples, fromFile.SelectMany(piece => piece));
            Assert.Equal(memory.Text, file.Text);
            Assert.Equal(
                fromMemory.Select((_, index) =>
                    TimeSpan.FromSeconds(fromMemory.Take(index).Sum(piece => piece.Length) / 16_000.0)),
                memory.Words.Select(word => word.Start));
            Assert.Equal(TimeSpan.FromSeconds(seconds), memory.AudioDuration);
            Assert.Equal(TimeSpan.FromSeconds(seconds), file.AudioDuration);
            List<int> sizes = engine.Sizes;
            // An engine handed minutes in one call is what this replaces; upstream also found its
            // runtime dropping speech from inputs over 30 s.
            Assert.True(sizes.Max() <= 20 * 16_000, "pieces are at most the server's 20 s");
            Assert.True(sizes.Count > 2);
            Assert.Equal(TimeSpan.FromSeconds(0.1) * (sizes.Count / 2), memory.ProcessingDuration);
        }
    }

    [Fact]
    public async Task ShortInputKeepsDirectEnginePath()
    {
        var engine = new PieceEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare("/unused");
        await transcriber.Transcribe(Enumerable.Repeat(1f, 30 * 16_000).ToArray());
        Assert.Equal([30 * 16_000], engine.Sizes);
    }

    [Fact]
    public async Task CancellationDoesNotStartTheRestOfALongInput()
    {
        var engine = new PieceEngine(gated: true);
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare("/unused");
        float[] samples = Speech(90);
        using var cancellation = new CancellationTokenSource();
        Task<ASRResult> work = Task.Run(() => transcriber.Transcribe(samples, cancellation.Token));
        await WaitFor(() => engine.Sizes.Count > 0);
        // One residency spans the complete recording.
        Assert.False(await transcriber.UnloadIfIdle());
        cancellation.Cancel();
        engine.Open();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        List<int> sizes = engine.Sizes;
        Assert.Single(sizes);
        Assert.True(sizes[0] <= 30 * 16_000);
    }

    [Fact]
    public async Task ForcedReloadCannotMixModelGenerationsWithinARecording()
    {
        var engine = new PieceEngine(gated: true);
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare("/unused");
        float[] samples = Speech(90);
        Task<ASRResult> work = Task.Run(() => transcriber.Transcribe(samples));
        await WaitFor(() => engine.Sizes.Count > 0);
        await transcriber.Unload();
        await transcriber.Prepare("/unused");
        engine.Open();
        // An old take must not use the replacement model.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Single(engine.Sizes);
    }

    [Fact]
    public async Task FailedMiddleChunkNeverReturnsAPartialSuccess()
    {
        var engine = new PieceEngine(failAt: 2);
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare("/unused");
        // A missing middle must fail the whole result.
        await Assert.ThrowsAsync<ASREngineError.InferenceFailed>(() => transcriber.Transcribe(Speech(90)));
        Assert.Equal(2, engine.Sizes.Count);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int index = 0; index < 2_000 && !condition(); index++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// Noise whose loudness changes every 20 ms, as syllables do. A constant signal is no speech
    /// to the planner's gate.
    /// </summary>
    /// <remarks>
    /// Five times upstream's level: every piece then peaks past <see cref="InputGain.TargetPeak"/>,
    /// so the level rule in front of the engine leaves it alone and the engine receives exactly
    /// the recorded samples.
    /// </remarks>
    private static float[] Speech(int seconds) =>
        Enumerable.Range(0, seconds * 16_000).Select(index => FileAudioChunksTests.Syllables(index, 0.5f)).ToArray();

    /// <summary>
    /// Records every piece; one word per piece, at its start, naming its length. The Swift
    /// original is an actor; a lock serializes the bookkeeping here.
    /// </summary>
    private sealed class PieceEngine(bool gated = false, int? failAt = null) : IASREngineAdapting
    {
        private readonly Lock gate = new();
        private readonly List<float[]> received = [];
        private TaskCompletionSource? opened;

        public List<int> Sizes { get { lock (gate) { return received.Select(piece => piece.Length).ToList(); } } }

        public List<float[]> Received { get { lock (gate) { return [.. received]; } } }

        public Task LoadModels(string directory) => Task.CompletedTask;

        public Task Unload() => Task.CompletedTask;

        public void Open()
        {
            TaskCompletionSource? current;
            lock (gate)
            {
                current = opened;
                opened = null;
            }

            current?.SetResult();
        }

        public async Task<ASRResult> Transcribe(float[] samples)
        {
            TaskCompletionSource? wait = null;
            lock (gate)
            {
                received.Add((float[])samples.Clone());
                if (received.Count == failAt) { throw new ASREngineError.InferenceFailed("test failure"); }
                if (gated)
                {
                    opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = opened;
                }
            }

            if (wait is not null) { await wait.Task.ConfigureAwait(false); }

            string text = samples.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var words = new[] { new ASRResult.Word(text, TimeSpan.Zero, TimeSpan.FromSeconds(0.1)) };
            return new ASRResult(
                text,
                words,
                TimeSpan.FromSeconds(samples.Length / 16_000.0),
                TimeSpan.FromSeconds(0.1));
        }
    }
}
