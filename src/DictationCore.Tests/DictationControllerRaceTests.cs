namespace DictationCore.Tests;

// MARK: - Edges that can be controlled by beats

/// <summary>
/// Gate: holds the call until the test opens it.
///
/// Cancellation does not interrupt an already started wait - it only marks it.
/// The gate reproduces this honestly: the continuation wakes up when the test
/// decides, not when the cancellation fired. It is into this gap that the tails
/// of cancelled sessions fall.
/// </summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Pass() => opened.Task;

    public void Open() => opened.TrySetResult();
}

/// <summary>
/// A microphone that shows whether it is turned on right now.
///
/// Behaves like a real engine: it does not start a second recording on top of
/// the current one, it refuses. Without this the test could not tell "the
/// microphone was muted" from "the microphone was left on", and that is the
/// main promise of the product.
/// </summary>
internal sealed class TrackedCapture : IAudioCapturing
{
    private readonly Lock gate = new();
    private int startCount;
    private int inFlight;
    private bool isRecording;
    private Gate? startGate;
    private Gate? abortGate;
    private readonly string file = Path.Combine(Path.GetTempPath(), "tracked-take.wav");

    public int StartCount { get { lock (gate) { return startCount; } } }

    public bool IsRecording { get { lock (gate) { return isRecording; } } }

    public int InFlight { get { lock (gate) { return inFlight; } } }

    public void SetStartGate(Gate? value) { lock (gate) { startGate = value; } }

    public void SetAbortGate(Gate? value) { lock (gate) { abortGate = value; } }

    public async Task<string> StartRecording()
    {
        Gate? waiting;
        lock (gate)
        {
            startCount += 1;
            inFlight += 1;
            waiting = startGate;
        }

        try
        {
            // The wait is outside the lock, exactly as the Swift actor released
            // its isolation at this suspension point.
            if (waiting is not null) { await waiting.Pass(); }

            lock (gate)
            {
                if (isRecording) { throw new AudioCaptureError.EngineUnavailable("запись уже идёт"); }
                isRecording = true;
                return file;
            }
        }
        finally
        {
            lock (gate) { inFlight -= 1; }
        }
    }

    public Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        lock (gate)
        {
            inFlight += 1;
            try
            {
                if (!isRecording) { throw new AudioCaptureError.NotRecording(); }
                isRecording = false;
                return Task.FromResult((file, TimeSpan.FromSeconds(2)));
            }
            finally
            {
                inFlight -= 1;
            }
        }
    }

    public async Task AbortRecording()
    {
        Gate? waiting;
        lock (gate) { waiting = abortGate; }

        if (waiting is not null) { await waiting.Pass(); }

        lock (gate) { isRecording = false; }
    }
}

internal sealed class RecordingInserter : ITextInserting
{
    private readonly Lock gate = new();
    private readonly List<string> insertedTexts = [];

    public IReadOnlyList<string> InsertedTexts { get { lock (gate) { return insertedTexts.ToArray(); } } }

    public Task Insert(string text, TargetApplication? target)
    {
        lock (gate) { insertedTexts.Add(text); }
        return Task.CompletedTask;
    }

    public Task PressReturn() => Task.CompletedTask;

    public TargetApplication? FrontmostApplication() => null;
}

internal sealed class QuietOverlay : IOverlayPresenting
{
    public Task Present(DictationState state, TimeSpan elapsed) => Task.CompletedTask;

    public Task Dismiss() => Task.CompletedTask;

    public Task PresentNotice(DictationNotice notice) => Task.CompletedTask;
}

internal sealed class CountingSounds : ISounding
{
    private int attentionPlays;

    public int AttentionPlays => Volatile.Read(ref attentionPlays);

    public Task PlayAttention()
    {
        Interlocked.Increment(ref attentionPlays);
        return Task.CompletedTask;
    }
}

/// <summary>Counter of recognition calls, reachable from the transcribe delegate.</summary>
internal sealed class TranscribeTracker
{
    private readonly Lock gate = new();
    private int inFlight;
    private int count;

    public int InFlight { get { lock (gate) { return inFlight; } } }

    public int Count { get { lock (gate) { return count; } } }

    public void Enter() { lock (gate) { count += 1; inFlight += 1; } }

    public void Leave() { lock (gate) { inFlight -= 1; } }
}

/// <summary>Reproducible generator: a failed run must be repeatable by its number.</summary>
internal struct SeededGenerator
{
    private ulong state;

    public SeededGenerator(ulong seed)
    {
        unchecked
        {
            state = (seed * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL;
        }
    }

    public ulong Next()
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }
}

// MARK: - Race tests

/// <summary>
/// Races between the user's gestures and the tails of already cancelled sessions.
///
/// Nothing checked here can be caught by reading the code: cancellation marks a
/// wait but does not interrupt it, and the continuation wakes up inside someone
/// else's session.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class DictationControllerRaceTests
{
    private TrackedCapture capture = new();
    private RecordingInserter inserter = new();
    private QuietOverlay overlay = new();
    private CountingSounds sounds = new();
    private TranscribeTracker transcribes = new();

    private DictationController MakeController(
        string recognized = "привет мир",
        Gate? transcribeGate = null)
    {
        TranscribeTracker tracker = transcribes;
        return new DictationController(
            capture: capture,
            transcribe: async (_, _) =>
            {
                tracker.Enter();
                try
                {
                    if (transcribeGate is not null) { await transcribeGate.Pass(); }
                    return new ASRResult(
                        recognized,
                        audioDuration: TimeSpan.FromSeconds(2),
                        processingDuration: TimeSpan.FromSeconds(0.1));
                }
                finally
                {
                    tracker.Leave();
                }
            },
            inserter: inserter,
            overlay: overlay,
            sounds: sounds);
    }

    private static async Task Settle(int iterations = 12)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    /// <summary>Wait until every tail has settled: the state and the edges of the system alike.</summary>
    private async Task Quiesce(DictationController controller, int limit = 400)
    {
        for (int i = 0; i < limit; i += 1)
        {
            if (controller.State == DictationState.Idle && capture.InFlight == 0 && transcribes.InFlight == 0)
            {
                break;
            }

            await Task.Yield();
            await Task.Delay(1);
        }

        await Settle(4);
    }

    // MARK: - Tail of the cancelled session

    [Fact]
    public async Task TestStaleFinalizationDoesNotTearDownTheNextSession()
    {
        // Recognition hangs in the gate. Cancellation marks it but does not wake
        // it - exactly like a real recognition finishing its buffer.
        var gate = new Gate();
        var controller = MakeController(transcribeGate: gate);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();
        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        await Settle();
        // The cancellation must close the session at once.
        Assert.Equal(DictationState.Idle, controller.State);

        // The person does not wait: they dictate again immediately.
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        // The tail of the cancelled session wakes up now.
        gate.Open();
        await Settle(20);

        // The tail of a cancelled session must not put out the new one.
        Assert.Equal(DictationState.Listening, controller.State);
        // The microphone of the new session must stay on.
        Assert.True(capture.IsRecording);

        // And the new session must reach the insertion normally.
        controller.Stop();
        await Quiesce(controller);
        // The second dictation must not vanish.
        Assert.Equal(["Привет мир"], inserter.InsertedTexts);
    }

    [Fact]
    public async Task TestCancelReturnsControllerToIdleWithoutAwaitingWedgedAbort()
    {
        var gate = new Gate();
        capture.SetAbortGate(gate);
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);
        controller.Cancel();

        for (int i = 0; i < 200 && controller.State != DictationState.Idle; i += 1)
        {
            await Task.Yield();
            await Task.Delay(1);
        }

        Assert.True(controller.State == DictationState.Idle, "a cancellation-deaf device edge cannot own UI liveness");

        gate.Open();
        await Quiesce(controller);
    }

    /// <summary>
    /// The hands-free callback is reentrant and may stop the session at once. The stop it
    /// accepts must not be cleared afterwards by the promotion that raised it.
    /// </summary>
    [Fact]
    public async Task TestHandsFreeCallbackCanStopReentrantlyWithoutLosingTheStop()
    {
        var controller = MakeController();
        controller.OnHandsFreeChange = active =>
        {
            if (active) { controller.StopHandsFree(); }
        };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.PromoteToHandsFree();
        await Quiesce(controller);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Single(inserter.InsertedTexts);
    }

    /// <summary>
    /// An idle observer is allowed to start the next session synchronously. The cleanup that
    /// published the idle must not then take that session's panel or state away.
    /// </summary>
    [Fact]
    public async Task TestIdleObserverCanStartNextSessionWithoutOldCleanupOrphaningIt()
    {
        var controller = MakeController();
        bool restarted = false;
        controller.OnStateChange = state =>
        {
            if (state != DictationState.Idle || restarted) { return; }
            restarted = true;
            controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        for (int i = 0; i < 300 && (controller.State != DictationState.Listening || !restarted); i += 1)
        {
            await Task.Yield();
            await Task.Delay(1);
        }

        Assert.True(restarted);
        Assert.Equal(DictationState.Listening, controller.State);
        Assert.True(capture.IsRecording);

        controller.Stop();
        await Quiesce(controller);
        Assert.Equal(2, inserter.InsertedTexts.Count);
    }

    [Fact]
    public async Task TestStaleStartDoesNotAdoptTheNextSession()
    {
        // The recording rises slowly: the key is released and cancelled earlier.
        var gate = new Gate();
        capture.SetStartGate(gate);
        var controller = MakeController();
        int failures = 0;
        controller.OnNotice = notice =>
        {
            if (notice.Kind == DictationNoticeKind.Failure) { Interlocked.Increment(ref failures); }
        };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle(3);
        controller.Cancel();
        await Settle(3);
        Assert.Equal(DictationState.Idle, controller.State);

        // The second session begins while the first still hangs in the engine startup.
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle(3);

        gate.Open();
        await Quiesce(controller);

        // Two outcomes are legitimate here: the stale start aborts in time and
        // the second session records, or the capture honestly refuses the
        // overlapping start and the second session fails with a notice. The
        // sound must follow the notice exactly - never fire without one.
        Assert.Equal(Volatile.Read(ref failures), sounds.AttentionPlays);

        // The main thing: after everything has calmed down, the microphone must be off.
        controller.Cancel();
        await Quiesce(controller);
        // An abandoned start must not leave the microphone on.
        Assert.False(capture.IsRecording);
    }

    // MARK: - Storm of gestures

    [Fact]
    public async Task TestRandomGestureStormsAlwaysLeaveTheMicrophoneOff()
    {
        // Hundreds of random turns. It is not the "correct" outcome of each that
        // is checked - there are far too many - but the two promises that must
        // always hold: the microphone is off, and the next dictation works.
        for (int seed = 0; seed < 200; seed += 1)
        {
            capture = new TrackedCapture();
            inserter = new RecordingInserter();
            overlay = new QuietOverlay();
            sounds = new CountingSounds();
            transcribes = new TranscribeTracker();

            var controller = MakeController();
            var rng = new SeededGenerator((ulong)seed);

            for (int step = 0; step < 6; step += 1)
            {
                switch ((int)(rng.Next() % 7))
                {
                    case 0: controller.Begin(handsFree: false, isEnabled: true, isModelReady: true); break;
                    case 1: controller.Begin(handsFree: true, isEnabled: true, isModelReady: true); break;
                    case 2: controller.Stop(); break;
                    case 3: controller.Cancel(); break;
                    case 4: controller.Interrupt(reason: "диск заполнен"); break;
                    case 5: controller.PromoteToHandsFree(); break;
                    default: controller.StopHandsFree(); break;
                }

                int pause = (int)(rng.Next() % 4);
                if (pause > 0) { await Task.Delay(pause); }
                else { await Task.Yield(); }
            }

            // The key is always released at the end of the storm: the hand does not stay on the button.
            controller.Stop();
            controller.StopHandsFree();
            await Quiesce(controller);

            Assert.True(controller.State == DictationState.Idle, $"the session hung, seed {seed}");
            Assert.False(capture.IsRecording, $"the microphone was left on, seed {seed}");

            int insertedBefore = inserter.InsertedTexts.Count;
            int startsBefore = capture.StartCount;
            Assert.True(
                insertedBefore <= startsBefore,
                $"more insertions than recordings - the text doubled, seed {seed}");

            // There must be a way out of any storm: an ordinary dictation works.
            controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
            await Settle(6);
            controller.Stop();
            await Quiesce(controller);

            Assert.True(
                inserter.InsertedTexts.Count == insertedBefore + 1,
                $"after the storm dictation must work again, seed {seed}");
        }
    }
}
