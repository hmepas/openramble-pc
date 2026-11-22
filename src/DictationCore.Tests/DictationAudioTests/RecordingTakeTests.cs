using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>
/// The disk and memory branches of one recording, without a microphone.
///
/// A disk that fails on cue is simulated by closing the take's writer under it: every later
/// append is then refused exactly as a full disk refuses it.
/// </summary>
public sealed class RecordingTakeTests : IDisposable
{
    private const int Frame = 2048;

    private readonly string directory;

    public RecordingTakeTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"take-{Guid.NewGuid()}");
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

    private sealed class Events
    {
        private readonly Lock gate = new();
        private readonly List<RecordingTake.Event> raised = [];

        public IReadOnlyList<RecordingTake.Event> Raised { get { lock (gate) { return [.. raised]; } } }

        public void Record(RecordingTake.Event happened)
        {
            lock (gate) { raised.Add(happened); }
        }
    }

    private WAVWriter OpenWriter()
    {
        var writer = new WAVWriter(Path.Combine(directory, $"{Guid.NewGuid():N}.wav"));
        writer.Open();
        return writer;
    }

    private static void Feed(RecordingTake take, float level, double seconds)
    {
        int remaining = (int)(seconds * 16_000);
        while (remaining > 0)
        {
            int count = Math.Min(Frame, remaining);
            take.Deliver(Enumerable.Repeat(level, count).ToArray());
            remaining -= count;
        }
    }

    /// <summary>A short segment floor, so a ten-second fixture can earn a cut.</summary>
    private static SpeechSegmenter Segmenter =>
        new(new SpeechSegmenter.Parameters(
            minimumPause: TimeSpan.FromMilliseconds(700),
            minimumSegment: TimeSpan.FromSeconds(4)));

    [Fact]
    public async Task TestTheCapAfterADiskFailureKeepsThePrefixAndSaysSoOnce()
    {
        var events = new Events();
        WAVWriter writer = OpenWriter();
        var take = new RecordingTake(writer, maximumBufferedSamples: 3 * Frame, segmentSink: null, events.Record);

        writer.Close();
        for (int frame = 0; frame < 6; frame += 1) { take.Deliver(new float[Frame]); }
        await WaitFor(() => take.CurrentStorage == RecordingTake.Storage.MemoryFull);

        RecordingTake.Stopped stopped = await take.Stop(TimeSpan.FromSeconds(5));

        Assert.Equal([RecordingTake.Event.DiskFailed, RecordingTake.Event.MemoryLimitReached], events.Raised);
        Assert.Equal(RecordingTake.Storage.MemoryFull, stopped.Storage);
        Assert.Equal(TimeSpan.FromSeconds(3.0 * Frame / 16_000), stopped.Duration);
        Assert.Equal(3 * Frame, take.TakeSamples()?.Length);
    }

    [Fact]
    public async Task TestADiskFailureAfterMemoryWasGivenUpLosesTheTakeAndSaysSo()
    {
        var events = new Events();
        WAVWriter writer = OpenWriter();
        var take = new RecordingTake(writer, maximumBufferedSamples: 2 * Frame, segmentSink: null, events.Record);

        // Past the cap with a healthy file: memory is given up, the file carries the take.
        for (int frame = 0; frame < 3; frame += 1) { take.Deliver(new float[Frame]); }
        await WaitFor(() => take.CurrentStorage == RecordingTake.Storage.DiskOnly);
        Assert.Empty(events.Raised);

        writer.Close();
        take.Deliver(new float[Frame]);
        take.Deliver(new float[Frame]);
        await WaitFor(() => take.CurrentStorage == RecordingTake.Storage.Lost);
        await take.Stop(TimeSpan.FromSeconds(5));

        Assert.Equal([RecordingTake.Event.NothingKept], events.Raised);
        Assert.Equal(RecordingTake.Storage.Lost, take.CurrentStorage);
        Assert.Null(take.TakeSamples());
    }

    [Fact]
    public async Task TestAFrameMemoryRefusedNeverReachesTheSegmenter()
    {
        var segments = new List<float[]>();
        WAVWriter writer = OpenWriter();
        var take = new RecordingTake(
            writer,
            maximumBufferedSamples: (int)(10.5 * 16_000),
            segmentSink: segments.Add,
            static _ => { },
            segmenter: Segmenter);

        writer.Close();
        // Ten seconds of speech, then a pause that would earn a cut only if the audio past the
        // memory cap - which no copy of the take holds - were counted.
        Feed(take, 0.5f, seconds: 10);
        Feed(take, 0, seconds: 1.5);
        await take.Stop(TimeSpan.FromSeconds(5));

        int consumed = take.ConsumedSampleCount();
        float[]? kept = take.TakeSamples();
        Assert.NotNull(kept);
        Assert.True(
            consumed <= kept.Length,
            $"the stream claimed {consumed} samples of a take that holds {kept.Length}");
    }

    [Fact]
    public async Task TestAnAbandonedDrainNeverReportsIntoWhateverComesNext()
    {
        var events = new Events();
        using var release = new ManualResetEventSlim();
        WAVWriter writer = OpenWriter();
        var take = new RecordingTake(
            writer,
            maximumBufferedSamples: 16_000 * 60,
            segmentSink: _ => release.Wait(TimeSpan.FromSeconds(10)),
            events.Record,
            segmenter: Segmenter);

        // The first cut wedges the drain inside the segment sink, with more audio queued behind.
        Feed(take, 0.5f, seconds: 10);
        Feed(take, 0, seconds: 1.5);
        Feed(take, 0.5f, seconds: 3);

        Task<RecordingTake.Stopped> stopping = take.Stop(TimeSpan.FromMilliseconds(50));
        await Task.Delay(300);
        release.Set();
        RecordingTake.Stopped stopped = await stopping;
        // The frames still queued now meet a closed file.
        await Task.Delay(300);

        Assert.False(stopped.Drained);
        Assert.Equal(RecordingTake.Storage.DiskOnly, stopped.Storage);
        Assert.Empty(events.Raised);
        Assert.Equal(RecordingTake.Phase.Stopped, take.CurrentPhase);
    }

    /// <summary>
    /// An event decided while the take was recording is not raised after a stop has begun: the
    /// stop waits for the one being raised, and the next one sees the stop.
    /// </summary>
    [Fact]
    public async Task TestNoEventIsRaisedOnceAStopHasBegun()
    {
        using var reporting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var seen = new List<(RecordingTake.Event Event, RecordingTake.Phase Phase)>();
        RecordingTake? take = null;
        WAVWriter writer = OpenWriter();
        take = new RecordingTake(
            writer,
            maximumBufferedSamples: 5 * Frame / 2,
            segmentSink: null,
            happened =>
            {
                lock (seen) { seen.Add((happened, take!.CurrentPhase)); }
                if (happened != RecordingTake.Event.DiskFailed) { return; }

                reporting.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            });

        take.Deliver(new float[Frame]);
        take.Deliver(new float[Frame]);
        await WaitFor(() => writer.Duration >= TimeSpan.FromSeconds(2.0 * Frame / 16_000));
        writer.Close();
        // One frame that the disk refuses and memory has no room for: two events from one commit,
        // the second still pending while the first one's handler runs.
        take.Deliver(new float[Frame]);
        Assert.True(reporting.Wait(TimeSpan.FromSeconds(5)));

        Task stopping = Task.Run(take.BeginStopping);
        await Task.Delay(200);
        release.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await take.Stop(TimeSpan.FromSeconds(5));

        lock (seen)
        {
            Assert.Equal(RecordingTake.Event.DiskFailed, seen[0].Event);
            Assert.All(seen, raised => Assert.Equal(RecordingTake.Phase.Recording, raised.Phase));
        }
    }

    /// <summary>
    /// A WAV that fails at its very end - the header, the last flush - does not cost the take
    /// when memory still holds all of it: the stop answers from memory, as for any failed file.
    /// </summary>
    [Fact]
    public async Task TestAWriterThatCannotCloseLeavesTheTakeToMemory()
    {
        var events = new Events();
        WAVWriter writer = OpenWriter();
        var take = new RecordingTake(writer, maximumBufferedSamples: 16_000, segmentSink: null, events.Record);
        take.Deliver(Enumerable.Repeat(0.25f, Frame).ToArray());
        await WaitFor(() => writer.Duration >= TimeSpan.FromSeconds((double)Frame / 16_000));
        // Every frame is in; only the close is left to fail.
        writer.Close();

        RecordingTake.Stopped stopped = await take.Stop(TimeSpan.FromSeconds(5));

        Assert.True(stopped.Drained);
        Assert.Equal(RecordingTake.Storage.MemoryOnly, stopped.Storage);
        Assert.Equal(TimeSpan.FromSeconds((double)Frame / 16_000), stopped.Duration);
        Assert.Equal(writer.FilePath, stopped.Path);
        Assert.Equal(Frame, take.TakeSamples()?.Length);
        Assert.Empty(events.Raised);
    }

    [Fact]
    public async Task TestAHealthyTakeStopsWithBothCopies()
    {
        var events = new Events();
        var take = new RecordingTake(OpenWriter(), maximumBufferedSamples: 16_000, segmentSink: null, events.Record);
        take.Deliver(Enumerable.Repeat(0.25f, Frame).ToArray());

        RecordingTake.Stopped stopped = await take.Stop(TimeSpan.FromSeconds(5));

        Assert.True(stopped.Drained);
        Assert.Equal(RecordingTake.Storage.DiskAndMemory, stopped.Storage);
        Assert.Equal(TimeSpan.FromSeconds((double)Frame / 16_000), stopped.Duration);
        Assert.Equal(Frame, take.TakeSamples()?.Length);
        Assert.True(File.Exists(stopped.Path));
        Assert.Empty(events.Raised);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition never held");
            await Task.Delay(5);
        }
    }
}
