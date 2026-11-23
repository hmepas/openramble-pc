using System.Globalization;
using DictationCore;

namespace LocalASR.Tests;

/// <remarks>
/// Each file is one second of a constant level, and the engine answers with that level as a
/// digit, so a request names its file. Levels 0.7 and above, because a piece is levelled up to
/// 0.7 (<see cref="InputGain"/>) before the engine hears it - never down.
/// </remarks>
public sealed class FileTranscriberTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"files-{Guid.NewGuid():D}");

    public FileTranscriberTests()
    {
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

    /// <summary>
    /// Upstream: a fragment the engine gave up for dictation is decoded again, and a finished
    /// one never is. This port yields between pieces instead (see
    /// <see cref="LocalTranscriber.TranscribeChunk"/>): dictation that starts while a piece is
    /// recognized lets that piece finish and keeps it, and the next waits for the take to end.
    /// </summary>
    [Fact]
    public async Task YieldRetainsFinishedInputsAndCorruptFileDoesNotStopOthers()
    {
        string first = Recording("first", 0.7f);
        string second = Recording("second", 0.8f);
        string broken = Path.Combine(directory, "broken.wav");
        File.WriteAllText(broken, "not audio");
        string third = Recording("third", 0.9f);
        using var priority = new DictationPriority(Path.Combine(directory, "priority.lock"));
        var engine = new LevelEngine(gateAt: 2);
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        var results = new List<(int Index, FileOutcome Outcome)>();

        Task work = new FileTranscriber(transcriber, priority).Transcribe(
            [first, second, broken, third],
            (index, outcome) =>
            {
                lock (results) { results.Add((index, outcome)); }
                return Task.CompletedTask;
            });
        await engine.Gated.WaitAsync(TimeSpan.FromSeconds(10));
        // Dictation starts while the second file is being recognized.
        priority.SetActive(true);
        engine.Open();
        await Task.Delay(300);
        Assert.Equal([7, 8], engine.Requests);
        Assert.False(work.IsCompleted, "the next piece waits for the take to end");
        priority.SetActive(false);
        await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([0, 1, 2, 3], results.Select(result => result.Index));
        List<FileTranscript> valid = results
            .Where(result => result.Index != 2)
            .Select(result => Assert.IsType<FileOutcome.Transcribed>(result.Outcome).Transcript)
            .ToList();
        Assert.Equal(["7", "8", "9"], valid.Select(transcript => transcript.Text));
        Assert.All(valid, transcript =>
        {
            Assert.Single(transcript.Words);
            Assert.Equal(TimeSpan.FromSeconds(1), transcript.Duration);
        });
        Assert.IsType<FileOutcome.Failed>(results[2].Outcome);
        // A finished fragment is never decoded twice.
        Assert.Equal([7, 8, 9], engine.Requests);
    }

    [Fact]
    public async Task CancellationDoesNotPublishACompletedTranscriptOrStartAnotherFragment()
    {
        string path = Recording("cancel", 0.7f);
        var engine = new LevelEngine(gateAt: 1);
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        var results = new List<FileOutcome>();
        using var cancellation = new CancellationTokenSource();

        Task work = new FileTranscriber(transcriber).Transcribe(
            [path, path, path],
            (_, outcome) =>
            {
                results.Add(outcome);
                return Task.CompletedTask;
            },
            cancellationToken: cancellation.Token);
        await engine.Gated.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        engine.Open();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Empty(results);
        Assert.Single(engine.Requests);
    }

    /// <summary>
    /// Port addition: a file longer than the thirty seconds that go whole is cut, and the words
    /// come back timed against the file, in order.
    /// </summary>
    [Fact]
    public async Task ALongFileIsRecognizedInPiecesTimedAgainstTheFile()
    {
        float[] samples = Enumerable.Range(0, 51 * 16_000)
            .Select(index => FileAudioChunksTests.Syllables(index, 0.1f))
            .ToArray();
        string path = WavFixture.WriteSamples(directory, samples, "long");
        var engine = new LevelEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        var progress = new List<double>();

        FileTranscript transcript = await new FileTranscriber(transcriber)
            .Transcribe(path, (seconds, total) => progress.Add(seconds / total));

        Assert.Equal(4, engine.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(51), transcript.Duration);
        Assert.Equal(4, transcript.Words.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.1), transcript.Words[0].Start);
        Assert.True(transcript.Words.Zip(transcript.Words.Skip(1)).All(pair => pair.First.Start < pair.Second.Start));
        Assert.Equal(1.0, progress[^1], 3);
    }

    private string Recording(string name, float value) =>
        WavFixture.WriteSamples(directory, Enumerable.Repeat(value, 16_000).ToArray(), name);

    /// <summary>Answers with the piece's first sample as a digit; can hold one request open.</summary>
    private sealed class LevelEngine(int? gateAt = null) : IASREngineAdapting
    {
        private readonly Lock gate = new();
        private readonly List<int> requests = [];
        private readonly TaskCompletionSource gated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> Requests { get { lock (gate) { return [.. requests]; } } }

        /// <summary>Completes once the gated request has arrived.</summary>
        public Task Gated => gated.Task;

        public Task LoadModels(string directory) => Task.CompletedTask;

        public Task Unload() => Task.CompletedTask;

        public void Open() => opened.TrySetResult();

        public async Task<ASRResult> Transcribe(float[] samples)
        {
            int level = (int)MathF.Round(samples[0] * 10);
            int count;
            lock (gate)
            {
                requests.Add(level);
                count = requests.Count;
            }

            if (count == gateAt)
            {
                gated.TrySetResult();
                await opened.Task.ConfigureAwait(false);
            }

            string text = level.ToString(CultureInfo.InvariantCulture);
            return new ASRResult(
                text,
                [new ASRResult.Word(text, TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.8))],
                TimeSpan.FromSeconds(samples.Length / 16_000.0),
                TimeSpan.FromSeconds(0.1));
        }
    }
}

/// <summary>Ctrl-C reaches ffmpeg as well, and its exit may be seen before the token is cancelled.</summary>
public sealed class FileInterruptionTests
{
    private static AudioFileReader.Failure.Unreadable Decoder(bool bySignal) =>
        new("ffmpeg found no audio it could decode in the file") { EndedBySignal = bySignal };

    [Fact]
    public void ADecoderKilledJustBeforeTheCancellationIsAnInterruption()
    {
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.True(FileTranscriber.Interrupted(Decoder(bySignal: true), source.Token));
    }

    [Fact]
    public void ADecoderKilledWithNoCancellationIsAFailedFile()
    {
        using var source = new CancellationTokenSource();
        Assert.False(FileTranscriber.Interrupted(Decoder(bySignal: true), source.Token));
    }

    [Fact]
    public void AnUndecodableFileFailsAtOnceAndAnyFailureAfterCancellationIsAnInterruption()
    {
        using var source = new CancellationTokenSource();
        Assert.False(FileTranscriber.Interrupted(Decoder(bySignal: false), source.Token));

        source.Cancel();
        Assert.True(FileTranscriber.Interrupted(new IOException("broken pipe"), source.Token));
    }
}
