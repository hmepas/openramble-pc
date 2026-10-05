namespace DictationCore.Tests;

// MARK: - False edges of the system

/// <remarks>
/// The Swift originals are actors; here a <see cref="Lock"/> serializes the
/// counters and the scripted answers. The setters are plain synchronous
/// methods - in Swift they had to be <c>await</c>ed only because the actor
/// isolated them.
/// </remarks>
internal sealed class FakeCapture : IAudioCapturing
{
    private readonly Lock gate = new();
    private int startCount;
    private int stopCount;
    private int abortCount;
    private AudioCaptureError? startError;
    private float[]? bufferedSamples;
    private readonly string file = Path.Combine(Path.GetTempPath(), "fake-take.wav");

    public int StartCount { get { lock (gate) { return startCount; } } }

    public int StopCount { get { lock (gate) { return stopCount; } } }

    public int AbortCount { get { lock (gate) { return abortCount; } } }

    public void SetStartError(AudioCaptureError? error) { lock (gate) { startError = error; } }

    public void SetBufferedSamples(float[]? samples) { lock (gate) { bufferedSamples = samples; } }

    public Task<string> StartRecording()
    {
        lock (gate)
        {
            startCount += 1;
            if (startError is { } error) { throw error; }
            return Task.FromResult(file);
        }
    }

    public Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        lock (gate)
        {
            stopCount += 1;
            return Task.FromResult((file, TimeSpan.FromSeconds(2)));
        }
    }

    public Task<float[]?> TakeBufferedSamples()
    {
        lock (gate)
        {
            float[]? samples = bufferedSamples;
            bufferedSamples = null;
            return Task.FromResult(samples);
        }
    }

    public Task AbortRecording()
    {
        lock (gate) { abortCount += 1; }
        return Task.CompletedTask;
    }
}

/// <summary>
/// A capture in which the first frame of sound does not come immediately, like the real one.
///
/// The real engine returns from <c>StartRecording</c> at startup, and hearing
/// begins about 0.13 s later. Everything that happens in this gap the fake
/// capture hides completely by having no delay at all.
/// </summary>
internal sealed class SlowFirstFrameCapture : IAudioCapturing
{
    private readonly Lock gate = new();
    private int startCount;
    private int stopCount;
    private int abortCount;

    /// <summary><c>null</c> - the device is silent and there will be no frame at all (AirPods in a drawer).</summary>
    private readonly Gate? firstFrame;
    private readonly string file = Path.Combine(Path.GetTempPath(), "slow-first-frame.wav");

    public SlowFirstFrameCapture(Gate? firstFrame) { this.firstFrame = firstFrame; }

    public int StartCount { get { lock (gate) { return startCount; } } }

    public int StopCount { get { lock (gate) { return stopCount; } } }

    public int AbortCount { get { lock (gate) { return abortCount; } } }

    public Task<string> StartRecording()
    {
        lock (gate) { startCount += 1; }
        return Task.FromResult(file);
    }

    public async Task<bool> WaitForFirstFrame()
    {
        if (firstFrame is null)
        {
            // Silent device: wait until recording finishes outside.
            while (StopCount == 0 && AbortCount == 0)
            {
                await Task.Yield();
                await Task.Delay(1);
            }

            return false;
        }

        await firstFrame.Pass();
        return true;
    }

    public Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        lock (gate) { stopCount += 1; }
        return Task.FromResult((file, TimeSpan.FromSeconds(2)));
    }

    public Task AbortRecording()
    {
        lock (gate) { abortCount += 1; }
        return Task.CompletedTask;
    }
}

internal sealed class FakeInserter : ITextInserting
{
    private readonly Lock gate = new();
    private readonly List<string> insertedTexts = [];
    private readonly List<TargetApplication?> targets = [];
    private int returnPresses;
    private TextInsertionError? error;
    private TargetApplication? frontmost;

    public IReadOnlyList<string> InsertedTexts { get { lock (gate) { return insertedTexts.ToArray(); } } }

    public int ReturnPresses { get { lock (gate) { return returnPresses; } } }

    public IReadOnlyList<TargetApplication?> Targets { get { lock (gate) { return targets.ToArray(); } } }

    public TargetApplication? Frontmost
    {
        get { lock (gate) { return frontmost; } }
        set { lock (gate) { frontmost = value; } }
    }

    public void SetError(TextInsertionError? value) { lock (gate) { error = value; } }

    public Task Insert(string text, TargetApplication? target)
    {
        lock (gate)
        {
            if (error is { } failure) { throw failure; }
            insertedTexts.Add(text);
            targets.Add(target);
            return Task.CompletedTask;
        }
    }

    public Task PressReturn()
    {
        lock (gate)
        {
            if (error is { } failure) { throw failure; }
            returnPresses += 1;
            return Task.CompletedTask;
        }
    }

    public TargetApplication? FrontmostApplication() => Frontmost;
}

internal sealed class FakeOverlay : IOverlayPresenting
{
    private readonly Lock gate = new();
    private readonly List<DictationState> presentedStates = [];
    private readonly List<DictationNotice> notices = [];
    private int dismissCount;

    public IReadOnlyList<DictationState> PresentedStates { get { lock (gate) { return presentedStates.ToArray(); } } }

    public int DismissCount { get { lock (gate) { return dismissCount; } } }

    public IReadOnlyList<DictationNotice> Notices { get { lock (gate) { return notices.ToArray(); } } }

    public Task Present(DictationState state, TimeSpan elapsed)
    {
        lock (gate) { presentedStates.Add(state); }
        return Task.CompletedTask;
    }

    public Task Dismiss()
    {
        lock (gate) { dismissCount += 1; }
        return Task.CompletedTask;
    }

    public Task PresentNotice(DictationNotice notice)
    {
        lock (gate) { notices.Add(notice); }
        return Task.CompletedTask;
    }
}

internal sealed class FakeSounds : ISounding
{
    private int attentionPlays;

    public int AttentionPlays => Volatile.Read(ref attentionPlays);

    public Task PlayAttention()
    {
        Interlocked.Increment(ref attentionPlays);
        return Task.CompletedTask;
    }
}

// MARK: - Tests

[Collection(DictationControllerCollection.Name)]
public sealed class DictationControllerTests
{
    private readonly FakeCapture capture = new();
    private readonly FakeInserter inserter = new();
    private readonly FakeOverlay overlay = new();
    private readonly FakeSounds sounds = new();

    private DictationController MakeController(
        string recognized = "привет мир",
        TimeSpan transcribeDelay = default,
        Exception? transcribeError = null,
        IReadOnlyList<DictionaryReplacement>? replacements = null) =>
        new(
            capture: capture,
            transcribe: async (_, token) =>
            {
                if (transcribeDelay > TimeSpan.Zero) { await Task.Delay(transcribeDelay, token); }
                if (transcribeError is not null) { throw transcribeError; }
                return new ASRResult(
                    recognized,
                    audioDuration: TimeSpan.FromSeconds(2),
                    processingDuration: TimeSpan.FromSeconds(0.1));
            },
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            pipeline: () => new TextPipeline(replacements: replacements ?? []));

    /// <summary>Let the controller's background tasks finish their work.</summary>
    private static async Task Settle(int iterations = 12)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    private static async Task WaitForState(DictationController controller, DictationState state, int limit = 400)
    {
        for (int i = 0; i < limit && controller.State != state; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TestRecognizerUsesCaptureBufferWithoutReopeningTheWAV()
    {
        float[] expected = [0.1f, -0.2f, 0.3f];
        capture.SetBufferedSamples(expected);
        var route = new TranscriptionRouteLog();
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) =>
            {
                route.RecordFile();
                return Task.FromResult(new ASRResult(
                    "file",
                    audioDuration: TimeSpan.FromSeconds(2),
                    processingDuration: TimeSpan.FromSeconds(0.1)));
            },
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            transcribeSamples: (samples, _) =>
            {
                route.RecordSamples(samples);
                return Task.FromResult(new ASRResult(
                    "buffer",
                    audioDuration: TimeSpan.FromSeconds(2),
                    processingDuration: TimeSpan.FromSeconds(0.1)));
            });

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal(0, route.FileCalls);
        Assert.Equal(1, route.SampleCalls);
        Assert.Equal(expected, route.Samples);
        Assert.Equal(["Buffer"], inserter.InsertedTexts);
    }

    // MARK: Normal path

    [Fact]
    public async Task TestFullFlowInsertsProcessedText()
    {
        var controller = MakeController(recognized: "привет мир");
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);
        Assert.Equal(
            [DictationState.Preparing, DictationState.Listening],
            overlay.PresentedStates.Take(2));

        controller.Stop();
        await Settle();

        // The text must have gone through the pipeline.
        Assert.Equal(["Привет мир"], inserter.InsertedTexts);
        // After the insertion the session is closed.
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task TestRecordingReachesListeningWithoutASound()
    {
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);

        await Settle();

        Assert.Equal(DictationState.Listening, controller.State);
        // The panel is the signal that recording started; a chime here would
        // be a second, louder copy of what the person can already see.
        Assert.Equal(0, sounds.AttentionPlays);
    }

    [Fact]
    public async Task TestSlowFirstFrameNeitherHangsTheSessionNorSounds()
    {
        // The microphone takes 0.13-0.14 s on M4 Pro to deliver its first
        // frame (docs/benchmarks.md). The panel must promise "speak" without
        // waiting for it, the session must survive the wait, and nothing may
        // be played on the way - a working dictation is silent.
        var frame = new Gate();
        var slowCapture = new SlowFirstFrameCapture(firstFrame: frame);
        var controller = new DictationController(
            capture: slowCapture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        // The panel shows the recording right away.
        Assert.Equal(DictationState.Listening, controller.State);
        Assert.Equal(0, sounds.AttentionPlays);

        frame.Open();
        await Settle();

        // The arriving frame must not disturb the session.
        Assert.Equal(DictationState.Listening, controller.State);
        // Nothing about a healthy recording is worth a sound.
        Assert.Equal(0, sounds.AttentionPlays);
    }

    [Fact]
    public async Task TestSilentDeviceDoesNotHangTheSession()
    {
        // There are no frames at all - the wrong microphone was chosen. Promising
        // "speak" is impossible here, but suspending the session on that wait is
        // impossible too: stopping must work, otherwise the microphone stays on.
        var silentCapture = new SlowFirstFrameCapture(firstFrame: null);
        var controller = new DictationController(
            capture: silentCapture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);
        // A silent device gives no reason to invite anyone to speak.
        Assert.Equal(0, sounds.AttentionPlays);

        controller.Stop();
        await WaitForState(controller, DictationState.Idle);

        // The stop must go through even without a single frame.
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(1, silentCapture.StopCount);
    }

    [Fact]
    public async Task TestQuickTapStopsWithoutWaitingForTheFirstFrame()
    {
        // Pressed and released before the engine heard anything: waiting for a
        // frame to invite "speak" into an already finished recording is
        // pointless, and on a silent device that wait would leave the
        // microphone on.
        var silentCapture = new SlowFirstFrameCapture(firstFrame: null);
        var controller = new DictationController(
            capture: silentCapture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        controller.Stop();
        await WaitForState(controller, DictationState.Idle);

        // The deferred release does not wait for the first frame.
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(0, sounds.AttentionPlays);
    }

    // MARK: Release before ready

    [Fact]
    public async Task TestReleaseBeforeCaptureStartsIsNotLost()
    {
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        // The key was released while the engine was still rising - the most common
        // gesture for a short phrase.
        controller.Stop();
        await Settle();

        // The release must not be lost.
        Assert.Single(inserter.InsertedTexts);
        // The recording must not stay on.
        Assert.Equal(DictationState.Idle, controller.State);
    }

    // MARK: Uniqueness of finalization

    [Fact]
    public async Task TestRepeatedStopInsertsOnlyOnce()
    {
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        controller.Stop();
        controller.Stop();
        controller.Stop();
        await Settle();

        // The text must be inserted exactly once.
        Assert.Single(inserter.InsertedTexts);
    }

    [Fact]
    public async Task TestSecondBeginWhileBusyIsIgnored()
    {
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        // A second session on top of a busy one does not start.
        Assert.Equal(1, capture.StartCount);
    }

    /// <summary>
    /// The session number advances once per session that actually began, and never otherwise.
    /// </summary>
    /// <remarks>
    /// It is what the diagnostic log tags the recording, the recognition and the insertion of one
    /// dictation with, and a number that moved for a press the kernel refused would attach the
    /// next dictation's events to a session that never recorded anything. A press landing on a
    /// session that is still finishing is not a hypothetical: a virtual machine sends phantom key
    /// events mid-transcription, and the field log has one arriving in exactly that gap.
    /// </remarks>
    [Fact]
    public async Task TestSessionNumberAdvancesOnlyForASessionThatBegan()
    {
        var controller = MakeController();
        Assert.Equal(0, controller.CurrentSession);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(1, controller.CurrentSession);

        // Refused: the previous session is still busy.
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(1, controller.CurrentSession);

        controller.Stop();
        await Settle();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(2, controller.CurrentSession);
    }

    // MARK: Cancel

    [Fact]
    public async Task TestCancelDuringRecordingNeverInserts()
    {
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        controller.Cancel();
        await Settle();

        // After a cancellation no text is inserted.
        Assert.Empty(inserter.InsertedTexts);
        // The recording must be interrupted and the file thrown away.
        Assert.Equal(1, capture.AbortCount);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task TestCancelDuringTranscriptionNeverInserts()
    {
        // Recognition takes a noticeable amount of time - we manage to cancel in
        // the middle. The moment "recognition is underway" is caught by polling:
        // a fixed sleep on an overloaded runner sleeps longer than the whole
        // recognition.
        var controller = MakeController(transcribeDelay: TimeSpan.FromMilliseconds(800));
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForState(controller, DictationState.Transcribing);
        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        await WaitForState(controller, DictationState.Idle);
        Assert.Equal(DictationState.Idle, controller.State);

        // A cancellation during recognition must prevent the insertion.
        Assert.Empty(inserter.InsertedTexts);
    }

    // MARK: Errors

    [Fact]
    public async Task TestCaptureFailureSurfacesAndResets()
    {
        capture.SetStartError(new AudioCaptureError.MicrophonePermissionDenied());
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        // The failure must be shown, not swallowed.
        Assert.Equal(DictationNoticeKind.Failure, overlay.Notices[0].Kind);
    }

    [Fact]
    public async Task TestRecognitionFailureIsReported()
    {
        var controller = MakeController(transcribeError: new ASREngineError.InferenceFailed("сбой"));
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal(DictationNoticeKind.Failure, overlay.Notices[0].Kind);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task TestTextStaysInMemoryWhenInsertionFails()
    {
        // Insertion failed - the recognized text cannot be lost or written to disk.
        inserter.SetError(new TextInsertionError.AccessibilityPermissionDenied());
        var controller = MakeController(recognized: "важная мысль");

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal("Важная мысль", controller.PendingRecovery?.Text);
    }

    [Fact]
    public async Task TestSecureInputFailureExplainsItself()
    {
        // An active password field is not a product failure, and the message should reflect that.
        inserter.SetError(new TextInsertionError.SecureInputActive());
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        // The person must be told why the text did not land.
        Assert.Contains(overlay.Notices, notice => notice.Message.Contains("secure input", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestClipboardRestoreFailureDoesNotClaimInsertedTextWasLost()
    {
        inserter.SetError(new TextInsertionError.InsertedButClipboardRestoreFailed());
        var controller = MakeController(recognized: "текст уже вставлен");

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Null(controller.PendingRecovery);
        Assert.Contains(
            overlay.Notices,
            notice => notice.Message.Contains("The text was inserted", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cancellation the person did not ask for is not Escape. A dependency that throws its own
    /// cancellation while the session is live has failed to recognize, and the person hears so.
    /// </summary>
    [Fact]
    public async Task TestDependencyCancellationErrorIsAVisibleRecognitionFailureNotUserEscape()
    {
        List<DictationNotice> notices = [];
        var controller = MakeController(transcribeError: new OperationCanceledException());
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle(30);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Contains(notices, notice => notice.Kind == DictationNoticeKind.Failure);
        Assert.Equal(1, sounds.AttentionPlays);
    }

    [Fact]
    public async Task TestEngineCancelledErrorIsAVisibleRecognitionFailureNotUserEscape()
    {
        List<DictationNotice> notices = [];
        var controller = MakeController(transcribeError: new ASREngineError.Cancelled());
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle(30);

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Contains(notices, notice => notice.Kind == DictationNoticeKind.Failure);
        Assert.Equal(1, sounds.AttentionPlays);
    }

    // MARK: Insertion target

    [Fact]
    public async Task TestTargetUsesPrepopulatedSnapshotWithoutAKeyDownSystemQuery()
    {
        var cached = new TargetApplication("com.apple.TextEdit", 1, "TextEdit");
        inserter.Frontmost = new TargetApplication("com.apple.Safari", 2, "Safari");
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "cached target",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.01))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            targetApplicationSnapshot: () => cached);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal(cached, inserter.Targets.FirstOrDefault());
    }

    [Fact]
    public async Task TestTargetIsCapturedAtKeyDownNotAtInsertion()
    {
        var original = new TargetApplication(
            BundleIdentifier: "com.apple.TextEdit",
            ProcessIdentifier: 1,
            LocalizedName: "TextEdit");
        inserter.Frontmost = original;

        var controller = MakeController(transcribeDelay: TimeSpan.FromMilliseconds(60));
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        // While recognition is in progress, the user has switched to another application.
        inserter.Frontmost = new TargetApplication(
            BundleIdentifier: "com.apple.Safari",
            ProcessIdentifier: 2,
            LocalizedName: "Safari");
        controller.Stop();
        await Settle(30);

        // The text must land where it was dictated.
        Assert.Equal("com.apple.TextEdit", inserter.Targets[0]?.BundleIdentifier);
    }

    // MARK: Command at the end of the phrase

    [Fact]
    public async Task TestSafeBetaNeverPressesReturnFromSpeech()
    {
        var controller = MakeController(recognized: "готово отправь");
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal(["Готово отправь"], inserter.InsertedTexts);
        // A false trigger must not send the message.
        Assert.Equal(0, inserter.ReturnPresses);
    }

    [Fact]
    public async Task TestNewLineCommandArrivesAsTextNotAsKeypress()
    {
        // The "new line" must reach the input field as a line break. Pressing
        // Return is not allowed here: it would send a message in a messenger.
        var controller = MakeController(recognized: "первая строка новая строка");
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal(["Первая строка\n"], inserter.InsertedTexts);
        // Return would have sent the message instead of breaking the line.
        Assert.Equal(0, inserter.ReturnPresses);
    }

    // MARK: Empty result

    [Fact]
    public async Task TestEmptyRecognitionInsertsNothingAndDoesNotError()
    {
        // "No messages at all" used to be checked here. The requirement was
        // phrased as "staying silent is not an error", and that is true: an empty
        // result is not an error. But silence turned out to be the wrong answer:
        // a dimmed panel without text is indistinguishable from "inserted into
        // someone else's window", and the person went looking for a phrase that
        // was never there. What is required is checked instead: no failure,
        // nothing inserted, no voice kept.
        var controller = MakeController(recognized: "   ");
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Empty(inserter.InsertedTexts);
        // An empty result is not a failure.
        Assert.All(overlay.Notices, notice => Assert.NotEqual(DictationNoticeKind.Failure, notice.Kind));
        Assert.All(overlay.Notices, notice => Assert.Null(notice.RecoveryAudio));
        Assert.Equal(DictationState.Idle, controller.State);
    }

    // MARK: Microphone and cleaning

    [Fact]
    public async Task TestOverlayIsDismissedOnEveryPath()
    {
        // Successful path.
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();
        int afterSuccess = overlay.DismissCount;
        Assert.True(afterSuccess > 0);

        // Path with cancellation.
        var cancelled = MakeController();
        cancelled.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        cancelled.Cancel();
        await Settle();
        int afterCancel = overlay.DismissCount;
        // The indicator must be removed after a cancellation too.
        Assert.True(afterCancel > afterSuccess);
    }

    [Fact]
    public async Task TestDisabledOrMissingModelPreventsStart()
    {
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: false, isModelReady: true);
        await Settle(3);
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: false);
        await Settle(3);

        // Without a model, or with dictation switched off, recording never starts.
        Assert.Equal(0, capture.StartCount);
    }

    // MARK: Hands-free mode

    [Fact]
    public async Task TestHandsFreeIgnoresKeyRelease()
    {
        var controller = MakeController();
        controller.Begin(handsFree: true, isEnabled: true, isModelReady: true);
        await Settle();

        // In hands-free mode, releasing the key does nothing.
        controller.Stop();
        await Settle(4);
        // The recording must go on.
        Assert.Equal(DictationState.Listening, controller.State);

        controller.StopHandsFree();
        await Settle();
        Assert.Single(inserter.InsertedTexts);
    }

    /// <summary>Which of the two recognition routes the controller took.</summary>
    private sealed class TranscriptionRouteLog
    {
        private readonly Lock gate = new();
        private int fileCalls;
        private int sampleCalls;
        private float[] samples = [];

        public int FileCalls { get { lock (gate) { return fileCalls; } } }

        public int SampleCalls { get { lock (gate) { return sampleCalls; } } }

        public float[] Samples { get { lock (gate) { return samples; } } }

        public void RecordFile() { lock (gate) { fileCalls += 1; } }

        public void RecordSamples(float[] value)
        {
            lock (gate)
            {
                sampleCalls += 1;
                samples = value;
            }
        }
    }
}
