namespace DictationCore.Tests;

/// <summary>
/// A take recognized in pieces while it is still being spoken.
///
/// The unit tests around <see cref="SpeechSegmenter"/> and <see cref="StreamedSegmentRecognizer"/>
/// pin the parts. These pin the thing that actually ships: the controller taking what the
/// stream produced, adding the tail nobody has recognized yet, and inserting one sentence rather
/// than a pile of fragments.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class StreamedDictationTests
{
    /// <summary>
    /// A capture that hands over segments the way the microphone does, and then reports how
    /// much of the take left in them.
    /// </summary>
    private sealed class StreamingCapture : IAudioCapturing
    {
        private readonly Lock gate = new();
        private readonly string file = Path.Combine(Path.GetTempPath(), "streamed-take.wav");
        private readonly float[] samples;

        /// <summary>Sample counts to ship as segments before the key comes up.</summary>
        private readonly int[] segments;
        private Action<float[]>? sink;
        private int consumed;

        public StreamingCapture(float[] samples, int[] segments)
        {
            this.samples = samples;
            this.segments = segments;
        }

        public Task SetSegmentSink(Action<float[]>? sink)
        {
            lock (gate) { this.sink = sink; }
            return Task.CompletedTask;
        }

        public Task<string> StartRecording()
        {
            // Everything the segmenter would have found, handed over the way the capture's
            // queue hands it over: in order, while recording runs.
            Action<float[]>? deliver;
            lock (gate) { deliver = sink; }

            int offset = 0;
            foreach (int count in segments)
            {
                if (offset + count > samples.Length) { break; }
                deliver?.Invoke(samples[offset..(offset + count)]);
                offset += count;
            }

            lock (gate) { consumed = offset; }
            return Task.FromResult(file);
        }

        public Task<(string Url, TimeSpan Duration)> StopRecording() =>
            Task.FromResult((file, TimeSpan.FromSeconds(samples.Length / 16_000.0)));

        public Task<float[]?> TakeBufferedSamples() => Task.FromResult<float[]?>(samples);

        public Task<int> ConsumedSampleCount()
        {
            lock (gate) { return Task.FromResult(consumed); }
        }

        public Task AbortRecording() => Task.CompletedTask;
    }

    private static DictationController MakeController(
        StreamingCapture capture,
        FakeInserter inserter,
        Func<float[], string> onSamples) =>
        new(
            capture: capture,
            transcribe: (_, _) =>
            {
                Assert.Fail("a streamed take must not fall back to reopening the WAV");
                return Task.FromResult(new ASRResult("", TimeSpan.Zero, TimeSpan.Zero));
            },
            transcribeSamples: (samples, _) => Task.FromResult(new ASRResult(
                onSamples(samples),
                audioDuration: TimeSpan.FromSeconds(samples.Length / 16_000.0),
                processingDuration: TimeSpan.FromSeconds(0.01))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            pipeline: () => new TextPipeline());

    private static async Task Run(DictationController controller)
    {
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle(40);
    }

    private static async Task Settle(int iterations = 12)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TestTheStreamedPiecesAndTheTailBecomeOneSentence()
    {
        // Thirty seconds, cut into three pieces of eight, leaving six for the tail - the shape
        // of an ordinary long dictation.
        float[] samples = Enumerable.Repeat(0.5f, 30 * 16_000).ToArray();
        var capture = new StreamingCapture(samples, [8 * 16_000, 8 * 16_000, 8 * 16_000]);
        var inserter = new FakeInserter();
        var controller = MakeController(
            capture,
            inserter,
            chunk => chunk.Length == 6 * 16_000 ? "and the tail." : "A piece");

        await Run(controller);

        Assert.True(
            inserter.InsertedTexts.FirstOrDefault() == "A piece A piece A piece and the tail.",
            "the pieces recognized during the take must lead, in order, with the tail last");
    }

    [Fact]
    public async Task TestATailTooShortToTrustIsRecognizedWithTheSegmentBeforeIt()
    {
        // The key comes up half a second after a cut. On its own that tail is in the range
        // where this decoder is non-monotonic, so the segment before it must come back and the
        // two go through together.
        float[] samples = Enumerable.Repeat(0.5f, 20 * 16_000).ToArray();
        var capture = new StreamingCapture(samples, [8 * 16_000, 11 * 16_000 + 8_000]);
        var inserter = new FakeInserter();
        var controller = MakeController(
            capture,
            inserter,
            // The re-decode covers the last segment plus the stub tail.
            chunk => chunk.Length >= 11 * 16_000 ? "second and tail together." : "first");

        await Run(controller);

        Assert.True(
            // The polisher capitalizes, as it always has.
            inserter.InsertedTexts.FirstOrDefault() == "First second and tail together.",
            "a stub tail must never be handed to the engine on its own");
    }

    [Fact]
    public async Task TestATakeWithNoCutsBehavesExactlyAsItAlwaysDid()
    {
        float[] samples = Enumerable.Repeat(0.5f, 5 * 16_000).ToArray();
        var capture = new StreamingCapture(samples, []);
        var inserter = new FakeInserter();
        var controller = MakeController(capture, inserter, chunk =>
        {
            Assert.True(chunk.Length == samples.Length, "the whole take goes in one decode");
            return "one whole take.";
        });

        await Run(controller);
        Assert.Equal("One whole take.", inserter.InsertedTexts.FirstOrDefault());
    }
}
