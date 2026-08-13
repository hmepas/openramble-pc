using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// The engine lifecycle under concurrency: loads are single-flight, unloads never interleave
/// with work, and a discarded generation cannot resurrect.
///
/// Two callers race in production: the press-time warm-up and the transcribe closure's own
/// prepare. Without coalescing they start two 16-second model loads - doubling the memory
/// spike on exactly the machine whose memory pressure caused the unload in the first place.
/// </summary>
public class LocalTranscriberResidencyTests
{
    /// <summary>An engine whose load waits at a gate until the test releases it.</summary>
    private sealed class GatedEngine : IASREngineAdapting
    {
        private readonly Lock gate = new();
        private TaskCompletionSource? waiter;
        private bool gateOpen;
        private bool shouldFail;
        private bool loaded;

        public int LoadCount { get; private set; }

        public int UnloadCount { get; private set; }

        public void SetShouldFail(bool value)
        {
            lock (gate)
            {
                shouldFail = value;
            }
        }

        public void OpenGate()
        {
            TaskCompletionSource? pending;
            lock (gate)
            {
                gateOpen = true;
                pending = waiter;
                waiter = null;
            }

            pending?.SetResult();
        }

        public async Task LoadModels(string directory)
        {
            TaskCompletionSource? pending = null;
            lock (gate)
            {
                LoadCount++;
                if (!gateOpen)
                {
                    pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiter = pending;
                }
            }

            if (pending is not null)
            {
                await pending.Task.ConfigureAwait(false);
            }

            lock (gate)
            {
                if (shouldFail)
                {
                    throw new ASREngineError.ModelsUnavailable("gated failure");
                }

                loaded = true;
            }
        }

        public Task<ASRResult> Transcribe(float[] samples)
        {
            lock (gate)
            {
                if (!loaded)
                {
                    throw new ASREngineError.ModelsNotLoaded();
                }

                return Task.FromResult(
                    new ASRResult("ok", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.1)));
            }
        }

        public Task Unload()
        {
            lock (gate)
            {
                UnloadCount++;
                loaded = false;
                return Task.CompletedTask;
            }
        }
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "residency-model");

    private static async Task Settle(int iterations = 20)
    {
        for (int index = 0; index < iterations; index++)
        {
            await Task.Yield();
            await Task.Delay(2);
        }
    }

    /// <summary>Two concurrent prepares - one load.</summary>
    [Fact]
    public async Task ConcurrentPreparesCoalesceIntoOneLoad()
    {
        var engine = new GatedEngine();
        var transcriber = new LocalTranscriber(engine);

        Task first = Task.Run(() => transcriber.Prepare(directory));
        Task second = Task.Run(() => transcriber.Prepare(directory));
        await Settle();
        engine.OpenGate();
        await first;
        await second;

        Assert.Equal(1, engine.LoadCount);
        Assert.True(transcriber.IsPrepared);
    }

    /// <summary>A failed load reports to every waiter and does not poison retries.</summary>
    [Fact]
    public async Task FailedLoadPropagatesAndRetryLoadsAgain()
    {
        var engine = new GatedEngine();
        engine.SetShouldFail(true);
        engine.OpenGate();
        var transcriber = new LocalTranscriber(engine);

        await Assert.ThrowsAnyAsync<Exception>(() => transcriber.Prepare(directory));
        Assert.False(transcriber.IsPrepared);

        engine.SetShouldFail(false);
        await transcriber.Prepare(directory);

        Assert.Equal(2, engine.LoadCount);
        Assert.True(transcriber.IsPrepared);
    }

    /// <summary>
    /// A forced unload during a load wins: the finishing load is discarded, its caller sees
    /// cancellation, and the engine holds nothing.
    /// </summary>
    [Fact]
    public async Task ForcedUnloadDuringLoadDiscardsTheStaleGeneration()
    {
        var engine = new GatedEngine();
        var transcriber = new LocalTranscriber(engine);

        Task load = Task.Run(() => transcriber.Prepare(directory));
        await Settle();
        await transcriber.Unload();
        engine.OpenGate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(transcriber.IsPrepared);
        Assert.True(engine.UnloadCount >= 1, "the orphaned load's weights must be released");
    }

    /// <summary>
    /// Residency's polite unload refuses while work is in flight; the forced one (wedge
    /// recovery) still goes through.
    /// </summary>
    [Fact]
    public async Task UnloadIfIdleRefusesDuringLoad()
    {
        var engine = new GatedEngine();
        var transcriber = new LocalTranscriber(engine);

        Task load = Task.Run(() => transcriber.Prepare(directory));
        await Settle();
        Assert.False(await transcriber.UnloadIfIdle(), "a polite unload must never interleave with a load");

        engine.OpenGate();
        await load;
        Assert.True(await transcriber.UnloadIfIdle(), "idle after load - the polite unload proceeds");
        Assert.False(transcriber.IsPrepared);
    }
}
