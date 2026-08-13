using System.Text;
using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// Dummy engine: lets the transcriber's behavior be checked without downloading the 483 MB
/// model.
/// </summary>
/// <remarks>The Swift original is an <c>actor</c>; here a lock serializes the bookkeeping.</remarks>
internal sealed class StubEngine : IASREngineAdapting
{
    private readonly Lock gate = new();
    private bool loaded;
    private ASREngineError? error;
    private ASRResult result = new("распознано", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.1));
    private readonly List<float[]> receivedBatches = [];

    public int LoadCount { get; private set; }

    public int TranscribeCount { get; private set; }

    public int LastSampleCount { get; private set; }

    /// <summary>Every buffer the engine received - they show whether the recording was cut.</summary>
    public IReadOnlyList<float[]> ReceivedBatches
    {
        get
        {
            lock (gate)
            {
                return [.. receivedBatches];
            }
        }
    }

    public void SetError(ASREngineError? value)
    {
        lock (gate)
        {
            error = value;
        }
    }

    public void SetResult(ASRResult value)
    {
        lock (gate)
        {
            result = value;
        }
    }

    public Task LoadModels(string directory)
    {
        lock (gate)
        {
            if (error is not null)
            {
                throw error;
            }

            LoadCount++;
            loaded = true;
            return Task.CompletedTask;
        }
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        lock (gate)
        {
            TranscribeCount++;
            LastSampleCount = samples.Length;
            receivedBatches.Add(samples);
            if (error is not null)
            {
                throw error;
            }

            if (!loaded)
            {
                throw new ASREngineError.ModelsNotLoaded();
            }

            return Task.FromResult(result);
        }
    }

    public Task Unload()
    {
        lock (gate)
        {
            loaded = false;
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Holds the first inference open so the test can prove a real dictation waits for the
/// warm-up instead of running a second prediction concurrently.
/// </summary>
internal sealed class WarmupGateEngine : IASREngineAdapting
{
    private readonly Lock gate = new();
    private readonly HashSet<int> gatedCalls;
    private readonly Dictionary<int, TaskCompletionSource> callGates = [];
    private int active;

    public WarmupGateEngine(IEnumerable<int>? gatedCalls = null)
    {
        this.gatedCalls = (gatedCalls ?? [1]).ToHashSet();
    }

    public int CallCount { get; private set; }

    public int MaximumConcurrency { get; private set; }

    public Task LoadModels(string directory) => Task.CompletedTask;

    public async Task<ASRResult> Transcribe(float[] samples)
    {
        TaskCompletionSource? gateForCall = null;
        lock (gate)
        {
            CallCount++;
            active++;
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            if (gatedCalls.Contains(CallCount))
            {
                gateForCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                callGates[CallCount] = gateForCall;
            }
        }

        if (gateForCall is not null)
        {
            await gateForCall.Task.ConfigureAwait(false);
        }

        lock (gate)
        {
            active--;
        }

        return new ASRResult("ok", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.1));
    }

    public void OpenFirstCall() => OpenCall(1);

    public void OpenCall(int call)
    {
        TaskCompletionSource? gateForCall;
        lock (gate)
        {
            callGates.Remove(call, out gateForCall);
        }

        gateForCall?.SetResult();
    }

    public Task Unload() => Task.CompletedTask;
}

public class LocalTranscriberTests : IDisposable
{
    private readonly string directory;

    public LocalTranscriberTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"transcriber-{Guid.NewGuid():D}");
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

        GC.SuppressFinalize(this);
    }

    /// <summary>Let queued continuations run, the way the Swift tests yield to the actor.</summary>
    private static async Task Settle(int iterations = 20)
    {
        for (int index = 0; index < iterations; index++)
        {
            await Task.Yield();
            await Task.Delay(2);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int index = 0; index < 500 && !condition(); index++)
        {
            await Task.Yield();
            await Task.Delay(2);
        }
    }

    /// <summary>Write a short WAV: mono, 16 kHz - exactly what the dictation records.</summary>
    private string WriteWAV(double seconds = 0.5, double sampleRate = 16_000) =>
        WavFixture.WriteFloat32(directory, seconds, sampleRate, channels: 1);

    [Fact]
    public async Task TranscribeBeforePrepareFails()
    {
        var transcriber = new LocalTranscriber(new StubEngine());
        string path = WriteWAV();

        await Assert.ThrowsAsync<ASREngineError.ModelsNotLoaded>(
            () => transcriber.Transcribe(path));
    }

    [Fact]
    public async Task PrepareIsIdempotent()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);

        await transcriber.Prepare(directory);
        await transcriber.Prepare(directory);

        Assert.Equal(1, engine.LoadCount);
    }

    [Fact]
    public async Task InferenceWarmupRunsARepresentativeOneSecondBuffer()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);

        await transcriber.WarmUpInference();

        IReadOnlyList<float[]> batches = engine.ReceivedBatches;
        Assert.Single(batches);
        Assert.Equal(16_000, batches[0].Length);
        Assert.All(batches[0], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public async Task RealTranscriptionWaitsForInferenceWarmup()
    {
        var engine = new WarmupGateEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);

        Task warmup = Task.Run(() => transcriber.WarmUpInference());
        await WaitFor(() => engine.CallCount == 1);
        Task<ASRResult> recognition = Task.Run(() => transcriber.Transcribe([0.25f, -0.25f]));
        await Settle();

        Assert.Equal(1, engine.CallCount);
        engine.OpenFirstCall();
        await warmup;
        await recognition;
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(1, engine.MaximumConcurrency);
    }

    [Fact]
    public async Task CancelledTranscriptionDoesNotRunAfterInferenceWarmup()
    {
        var engine = new WarmupGateEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);

        Task warmup = Task.Run(() => transcriber.WarmUpInference());
        await WaitFor(() => engine.CallCount == 1);
        using var cancellation = new CancellationTokenSource();
        Task<ASRResult> recognition = Task.Run(
            () => transcriber.Transcribe([0.25f, -0.25f], null, cancellation.Token));
        await Settle();
        await cancellation.CancelAsync();
        engine.OpenFirstCall();

        await warmup;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognition);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task LateCancelledWarmupCannotClearNewGenerationWarmup()
    {
        var engine = new WarmupGateEngine([1, 2]);
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);

        Task oldWarmup = Task.Run(() => transcriber.WarmUpInference());
        await WaitFor(() => engine.CallCount >= 1);

        await transcriber.Unload();
        await transcriber.Prepare(directory);
        Task newWarmup = Task.Run(() => transcriber.WarmUpInference());
        await WaitFor(() => engine.CallCount >= 2);

        engine.OpenCall(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWarmup);

        Task<ASRResult> recognition = Task.Run(() => transcriber.Transcribe([0.25f, -0.25f]));
        await Settle();
        Assert.Equal(2, engine.CallCount);
        Assert.True(transcriber.IsBusy);

        engine.OpenCall(2);
        await newWarmup;
        await recognition;
        Assert.Equal(3, engine.CallCount);
    }

    [Fact]
    public async Task ReadsWavAndPassesSamplesToEngine()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        string path = WriteWAV(seconds: 0.5);

        ASRResult result = await transcriber.Transcribe(path);

        Assert.Equal("распознано", result.Text);
        // Half a second at 16 kHz is about 8000 samples.
        Assert.True(Math.Abs(engine.LastSampleCount - 8000) <= 200, $"got {engine.LastSampleCount}");
    }

    [Fact]
    public async Task ResamplesFileRecordedAtOtherRate()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        // An external microphone may well give 48 kHz - we bring it to 16 kHz ourselves.
        string path = WriteWAV(seconds: 1.0, sampleRate: 48_000);

        await transcriber.Transcribe(path);

        Assert.True(Math.Abs(engine.LastSampleCount - 16_000) <= 800, $"got {engine.LastSampleCount}");
    }

    [Fact]
    public async Task EmptyFileIsRejectedWithClearError()
    {
        var transcriber = new LocalTranscriber(new StubEngine());
        await transcriber.Prepare(directory);
        string path = Path.Combine(directory, "broken.wav");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("не звук"));

        await Assert.ThrowsAsync<ASREngineError.UnsupportedAudioFormat>(
            () => transcriber.Transcribe(path));
    }

    // MARK: - A long recording goes into the engine whole

    /// <summary>
    /// Recordings longer than twelve seconds used to be cut here at pauses. The cutting fell
    /// in the middle of a phrase, slowed parsing down twice and on mixed speech lost more
    /// text than the engine with its own window gluing (the measurements are in
    /// docs/benchmarks.md). The test guards against the cutting returning unnoticed.
    /// </summary>
    [Fact]
    public async Task LongRecordingGoesToEngineInOnePiece()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        // A minute: five times longer than the previous slicing threshold.
        float[] samples = Enumerable.Range(0, 60 * 16_000)
            .Select(index => (float)(Math.Sin(index * 0.05) * 0.3))
            .ToArray();

        await transcriber.Transcribe(samples);

        IReadOnlyList<float[]> batches = engine.ReceivedBatches;
        Assert.Single(batches);
        Assert.Equal(samples.Length, batches[0].Length);
    }

    /// <summary>
    /// The most dangerous case of the previous cutting: a short tail after the cut was
    /// dropped silently - the person said "yes", and that "yes" disappeared.
    /// </summary>
    [Fact]
    public async Task ShortTailAfterCutIsNotDropped()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);

        // 12.0 s of speech and 0.2 s at the very end. The previous cutting cut exactly at the
        // twelve-second threshold and did not give a remainder shorter than 0.35 s to the
        // engine at all - a "yes" at the end of a phrase vanished without a trace.
        const float TailMarker = 0.42f;
        float[] samples =
        [
            .. Enumerable.Range(0, (int)(12.0 * 16_000))
                .Select(index => (float)(Math.Sin(index * 0.05) * 0.3)),
            .. Enumerable.Repeat(TailMarker, (int)(0.2 * 16_000)),
        ];

        await transcriber.Transcribe(samples);

        float[] delivered = engine.ReceivedBatches.SelectMany(batch => batch).ToArray();
        Assert.Equal(samples.Length, delivered.Length);
        Assert.Equal(TailMarker, delivered[^1]);
    }

    /// <summary>
    /// Word timings come from the engine and must not change on the way: the previous gluing
    /// of pieces added a segment offset to them.
    /// </summary>
    [Fact]
    public async Task WordTimingsArePassedThroughUnchanged()
    {
        var engine = new StubEngine();
        engine.SetResult(new ASRResult(
            "раз два",
            [
                new ASRResult.Word("раз", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.6), 0.9),
                new ASRResult.Word("два", TimeSpan.FromSeconds(40.0), TimeSpan.FromSeconds(40.5), 0.8),
            ],
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(0.5)));
        var transcriber = new LocalTranscriber(engine);
        await transcriber.Prepare(directory);
        float[] samples = Enumerable.Range(0, 60 * 16_000)
            .Select(index => (float)(Math.Sin(index * 0.05) * 0.3))
            .ToArray();

        ASRResult result = await transcriber.Transcribe(samples);

        Assert.Equal("раз два", result.Text);
        Assert.Equal(
            [TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(40.0)],
            result.Words.Select(word => word.Start));
        Assert.Equal(
            [TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(40.5)],
            result.Words.Select(word => word.End));
    }

    [Fact]
    public async Task EmptyBufferIsRejected()
    {
        var transcriber = new LocalTranscriber(new StubEngine());
        await transcriber.Prepare(directory);

        await Assert.ThrowsAsync<ASREngineError.UnsupportedAudioFormat>(
            () => transcriber.Transcribe([]));
    }

    [Fact]
    public async Task UnloadRequiresPrepareAgain()
    {
        var transcriber = new LocalTranscriber(new StubEngine());
        await transcriber.Prepare(directory);
        Assert.True(transcriber.IsPrepared);

        await transcriber.Unload();

        Assert.False(transcriber.IsPrepared);
    }
}
