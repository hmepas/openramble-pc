using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>
/// A queue of frames between the audio stream and the disk.
///
/// Both defects that are guarded here are silent: the recording is obtained, the file
/// opens, and only the speech in it is not the same as what was said. Checked
/// on a real recording in WAV, because it is the one that can mix the frames.
/// </summary>
public sealed class FrameSinkTests : IDisposable
{
    private readonly string directory;

    public FrameSinkTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"frames-{Guid.NewGuid()}");
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

    /// <summary>Frame collector, repeating the work of the microphone: slow and serialized.</summary>
    private sealed class Collector
    {
        private readonly Lock gate = new();
        private readonly List<float> received = [];

        public float[] Received
        {
            get
            {
                lock (gate)
                {
                    return [.. received];
                }
            }
        }

        public async Task Consume(float[] samples)
        {
            // Real writing to disk is not instantaneous, and it is at this pause
            // that tasks overtook each other.
            await Task.Delay(1).ConfigureAwait(false);
            lock (gate)
            {
                received.AddRange(samples);
            }
        }
    }

    [Fact]
    public async Task TestFramesArriveInTheOrderTheyWereProduced()
    {
        var sink = new FrameSink();
        var collector = new Collector();
        var enqueue = sink.Start(collector.Consume);

        // 400 frames in a row, each marked with its own number.
        float[] expected = [.. Enumerable.Range(0, 400).Select(value => (float)value)];
        foreach (float value in expected)
        {
            enqueue([value]);
        }

        await sink.Finish();

        Assert.Equal(expected, collector.Received);
    }

    [Fact]
    public async Task TestOrderHoldsWhenFramesComeFromTheAudioThread()
    {
        // The engine callback does not come from the thread where the sink lives.
        var sink = new FrameSink();
        var collector = new Collector();
        var enqueue = sink.Start(collector.Consume);

        const int Count = 300;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioThread = new Thread(() =>
        {
            for (int index = 0; index < Count; index++)
            {
                enqueue([index]);
            }

            done.SetResult();
        });
        audioThread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sink.Finish();

        Assert.Equal(Enumerable.Range(0, Count).Select(value => (float)value), collector.Received);
    }

    [Fact]
    public async Task TestFinishWaitsForTheLastFrame()
    {
        // Tail of the phrase: the person releases the key exactly on the last word.
        var sink = new FrameSink();
        var collector = new Collector();
        var enqueue = sink.Start(collector.Consume);

        for (int index = 0; index < 200; index++)
        {
            enqueue([index]);
        }

        await sink.Finish();

        float[] received = collector.Received;
        Assert.Equal(200, received.Length);
        Assert.Equal(199, received[^1]);
    }

    [Fact]
    public async Task TestTailSurvivesIntoTheWAVFile()
    {
        // The same thing, but until the very end: the file on disk must contain everything.
        string path = Path.Combine(directory, "take.wav");
        var writer = new WAVWriter(path, sampleRate: 16_000, channels: 1);
        writer.Open();

        var sink = new FrameSink();
        var enqueue = sink.Start(async samples =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            writer.Append(samples);
        });

        // A second of sound in frames of 512 samples, as the engine produces.
        const int FrameCount = 16_000 / 512;
        for (int index = 0; index < FrameCount; index++)
        {
            enqueue([.. Enumerable.Repeat(0.25f, 512)]);
        }

        await sink.Finish();
        string closed = writer.Close();

        long size = new FileInfo(closed).Length;
        Assert.Equal(44 + (FrameCount * 512 * 2), size);
    }

    [Fact]
    public void TestCancelDropsTheQueueInsteadOfWritingIt()
    {
        // The dictation was cancelled - there is nothing to add.
        var sink = new FrameSink();
        var collector = new Collector();
        var enqueue = sink.Start(collector.Consume);

        for (int index = 0; index < 500; index++)
        {
            enqueue([index]);
        }

        sink.Cancel();

        Assert.True(
            collector.Received.Length < 500,
            "cancelling must not wait for the whole queue");
    }

    [Fact]
    public async Task TestEnqueueAfterFinishIsHarmless()
    {
        // The frame that came out of the hardware simultaneously with the stop arrives
        // after the queue is already closed. You cannot fall on this.
        var sink = new FrameSink();
        var collector = new Collector();
        var enqueue = sink.Start(collector.Consume);

        enqueue([1]);
        await sink.Finish();
        enqueue([2]);

        Assert.Equal([1f], collector.Received);
    }
}
