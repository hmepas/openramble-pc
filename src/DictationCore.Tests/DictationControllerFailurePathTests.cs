using System.Diagnostics;

namespace DictationCore.Tests;

// MARK: - False edges that can break in parts

internal sealed class ScriptedCapture : IAudioCapturing
{
    private readonly Lock gate = new();
    private int startCount;
    private int abortCount;
    private TimeSpan duration = TimeSpan.FromSeconds(2);
    private Exception? startError;
    private readonly string file = Path.Combine(Path.GetTempPath(), "scripted-take.wav");

    public int StartCount { get { lock (gate) { return startCount; } } }

    public int AbortCount { get { lock (gate) { return abortCount; } } }

    public void SetDuration(TimeSpan value) { lock (gate) { duration = value; } }

    public void SetStartError(Exception? error) { lock (gate) { startError = error; } }

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
        lock (gate) { return Task.FromResult((file, duration)); }
    }

    public Task AbortRecording()
    {
        lock (gate) { abortCount += 1; }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Pasting and pressing Return break independently: they are different system
/// calls, and the second one refuses to work while the first one is alive.
/// </summary>
internal sealed class ScriptedInserter : ITextInserting
{
    private readonly Lock gate = new();
    private readonly List<string> insertedTexts = [];
    private int returnPresses;
    private TextInsertionError? insertError;
    private TextInsertionError? returnError;
    private TimeSpan insertDelay;

    public IReadOnlyList<string> InsertedTexts { get { lock (gate) { return insertedTexts.ToArray(); } } }

    public int ReturnPresses { get { lock (gate) { return returnPresses; } } }

    public void SetInsertError(TextInsertionError? error) { lock (gate) { insertError = error; } }

    public void SetReturnError(TextInsertionError? error) { lock (gate) { returnError = error; } }

    public void SetInsertDelay(TimeSpan delay) { lock (gate) { insertDelay = delay; } }

    public async Task Insert(string text, TargetApplication? target)
    {
        TimeSpan delay;
        lock (gate) { delay = insertDelay; }
        if (delay > TimeSpan.Zero) { await Task.Delay(delay); }

        lock (gate)
        {
            if (insertError is { } error) { throw error; }
            insertedTexts.Add(text);
        }
    }

    public Task PressReturn()
    {
        lock (gate)
        {
            if (returnError is { } error) { throw error; }
            returnPresses += 1;
            return Task.CompletedTask;
        }
    }

    public TargetApplication? FrontmostApplication() => null;
}

internal sealed class CollectingOverlay : IOverlayPresenting
{
    private readonly Lock gate = new();
    private readonly List<DictationNotice> notices = [];

    public IReadOnlyList<DictationNotice> Notices { get { lock (gate) { return notices.ToArray(); } } }

    public Task Present(DictationState state, TimeSpan elapsed) => Task.CompletedTask;

    public Task Dismiss() => Task.CompletedTask;

    public Task PresentNotice(DictationNotice notice)
    {
        lock (gate) { notices.Add(notice); }
        return Task.CompletedTask;
    }
}

/// <summary>
/// A microphone that opens only when the test says so, and records exactly as long as it was
/// open: a take released before it opened holds nothing, the way the real device behaves.
/// </summary>
internal sealed class SlowOpeningMicrophone : IAudioCapturing
{
    private readonly Lock gate = new();
    private readonly Gate opening;
    private readonly string file = Path.Combine(Path.GetTempPath(), "slow-opening-take.wav");
    private long? openedAt;
    private int abortCount;

    public SlowOpeningMicrophone(Gate opening) { this.opening = opening; }

    public int AbortCount { get { lock (gate) { return abortCount; } } }

    public async Task<string> StartRecording()
    {
        await opening.Pass();
        lock (gate) { openedAt = Stopwatch.GetTimestamp(); }
        return file;
    }

    public Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        lock (gate)
        {
            TimeSpan open = openedAt is { } at ? Stopwatch.GetElapsedTime(at) : TimeSpan.Zero;
            openedAt = null;
            return Task.FromResult((file, open));
        }
    }

    public Task AbortRecording()
    {
        lock (gate)
        {
            abortCount += 1;
            openedAt = null;
        }

        return Task.CompletedTask;
    }
}

internal sealed class SilentSounds : ISounding
{
    public Task PlayAttention() => Task.CompletedTask;
}

internal sealed class StateLog
{
    private readonly Lock gate = new();
    private readonly List<DictationState> states = [];

    public IReadOnlyList<DictationState> States { get { lock (gate) { return states.ToArray(); } } }

    public void Append(DictationState state) { lock (gate) { states.Add(state); } }
}

// MARK: - Tests

/// <summary>
/// The paths the product takes when something goes wrong. They are the ones
/// that decide whether a person trusts dictation: the normal script is seen
/// every day, the failure behaviour once - and remembered for a long time.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class DictationControllerFailurePathTests
{
    private readonly ScriptedCapture capture = new();
    private readonly ScriptedInserter inserter = new();
    private readonly CollectingOverlay overlay = new();
    private readonly TranscribeCounter transcribeCalls = new();

    /// <summary>Recognition call count - the delegate runs off the test's thread.</summary>
    internal sealed class TranscribeCounter
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Increment() => Interlocked.Increment(ref count);
    }

    /// <summary>The clock that the test moves itself: one cannot wait an hour to check a limit.</summary>
    internal sealed class TestClock
    {
        private readonly Lock gate = new();
        private TimeSpan offset;

        public void Advance(TimeSpan by) { lock (gate) { offset += by; } }

        public DateTimeOffset Now { get { lock (gate) { return DateTimeOffset.Now + offset; } } }
    }

    private DictationController MakeController(
        string recognized = "привет мир",
        bool allowPressReturnCommand = false,
        TestClock? clock = null)
    {
        TranscribeCounter counter = transcribeCalls;
        return new DictationController(
            capture: capture,
            transcribe: (_, _) =>
            {
                counter.Increment();
                return Task.FromResult(new ASRResult(
                    recognized,
                    audioDuration: TimeSpan.FromSeconds(2),
                    processingDuration: TimeSpan.FromSeconds(0.1)));
            },
            inserter: inserter,
            overlay: overlay,
            sounds: new SilentSounds(),
            pipeline: _ => new TextPipeline(allowPressReturnCommand: allowPressReturnCommand),
            now: () => clock?.Now ?? DateTimeOffset.Now);
    }

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

    private static async Task RunFullDictation(DictationController controller)
    {
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();
    }

    /// <summary>
    /// The audio daemon is dead at press time - coreaudiod restarts were
    /// observed overnight in the field. One session fails with a plain notice,
    /// and the very NEXT press works once audio is back. A crashed engine
    /// bricking every hotkey until app restart is Handy 0.9.5's failure mode;
    /// ours must stay a one-session event.
    /// </summary>
    [Fact]
    public async Task TestCaptureStartFailureSurfacesAndTheNextPressWorks()
    {
        var controller = MakeController(recognized: "after recovery");
        capture.SetStartError(new AudioCaptureError.NotRecording());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();

        // A failed start must free the session.
        Assert.Equal(DictationState.Idle, controller.State);
        DictationNotice notice = overlay.Notices[^1];
        Assert.Equal(DictationNoticeKind.Failure, notice.Kind);
        Assert.Equal("Couldn't record audio.", notice.Message);

        capture.SetStartError(null);
        await RunFullDictation(controller);

        // The pipeline capitalizes the sentence start - that's the healthy path.
        Assert.Equal(["After recovery"], inserter.InsertedTexts);
    }

    /// <summary>
    /// However long the person speaks, the session is theirs to finish.
    /// A one-hour cap once lived here "for safety"; this pins its absence.
    /// </summary>
    [Fact]
    public async Task TestAThreeHourRecordingIsStillTheUsersToFinish()
    {
        var clock = new TestClock();
        var controller = MakeController(recognized: "длинная речь", clock: clock);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        clock.Advance(TimeSpan.FromHours(3));
        await Settle();
        // Nothing may stop a long recording but the person.
        Assert.Equal(DictationState.Listening, controller.State);

        controller.Stop();
        await Settle();
        Assert.Equal(["Длинная речь"], inserter.InsertedTexts);
    }

    // MARK: - Press Return

    [Fact]
    public async Task TestFailedReturnDoesNotPretendTheTextWasLost()
    {
        // The text was inserted, but pressing Return failed - for example, the
        // user is still holding the modifier. Saying "the text was not inserted,
        // it is saved" would be false twice over: the text is in place, and
        // nobody needs a second copy of it on disk - a private tool does not
        // stack up dictated text for no reason.
        inserter.SetReturnError(new TextInsertionError.ModifiersStillHeld());
        var controller = MakeController(recognized: "готово. отправь", allowPressReturnCommand: true);

        await RunFullDictation(controller);

        // The text must stay inserted.
        Assert.Equal(["Готово."], inserter.InsertedTexts);
        Assert.Equal(DictationNoticeKind.Warning, overlay.Notices[0].Kind);
        // The message must explain that it is the keypress that failed.
        Assert.Contains(overlay.Notices, notice => notice.Message.Contains("Return", StringComparison.Ordinal));
        // There is nothing to rescue - the text is in place.
        Assert.Null(controller.PendingRecovery);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task TestSuccessfulReturnLeavesNoNotice()
    {
        var controller = MakeController(recognized: "готово. отправь", allowPressReturnCommand: true);

        await RunFullDictation(controller);

        Assert.Equal(1, inserter.ReturnPresses);
        // A successful dictation must say nothing at all.
        Assert.Empty(overlay.Notices);
    }

    // MARK: - Save text

    [Fact]
    public async Task TestInsertionFailureKeepsTextOnlyInMemory()
    {
        inserter.SetInsertError(new TextInsertionError.AccessibilityPermissionDenied());
        var controller = MakeController(recognized: "важная мысль");

        await RunFullDictation(controller);

        Assert.Equal(DictationNoticeKind.Warning, overlay.Notices[0].Kind);
        Assert.Equal("Важная мысль", controller.PendingRecovery?.Text);
        // The session must close in any case.
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task TestNextDictationKeepsPreviousRecoverableTextUntilExplicitRecovery()
    {
        inserter.SetInsertError(new TextInsertionError.AccessibilityPermissionDenied());
        var controller = MakeController(recognized: "важная мысль");
        await RunFullDictation(controller);
        Assert.NotNull(controller.PendingRecovery);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);

        Assert.Equal("Важная мысль", controller.PendingRecovery?.Text);
    }

    // MARK: - Press too short

    [Fact]
    public async Task TestAccidentalTapIsDroppedWithoutBotheringTheEngine()
    {
        // A touch of the key - no recording. There is nothing to recognize, and
        // no reason to frighten anyone with an error either.
        capture.SetDuration(TimeSpan.FromSeconds(0.1));
        var controller = MakeController();

        await RunFullDictation(controller);

        // There is nothing to recognize.
        Assert.Equal(0, transcribeCalls.Count);
        Assert.Empty(inserter.InsertedTexts);
        // An accidental touch is no reason for a message.
        Assert.Empty(overlay.Notices);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    /// <summary>
    /// A key that went down and up in the same millisecond leaves nothing behind either.
    /// </summary>
    /// <remarks>
    /// Not a person's gesture - a QEMU/spice guest re-synchronising the Caps Lock LED sends a
    /// compensating down and up with the same timestamp, and there is nothing this side of the
    /// hook that can tell those from a real touch of the key. The gesture machine holds the
    /// release for the rest of the double-tap window, so the microphone does open and about
    /// three hundred milliseconds of room noise do reach the take - the exact length seen in the
    /// field log. That take must go the way every other accidental touch goes: not to the engine,
    /// which answers three hundred milliseconds of nothing with punctuation, and not to the
    /// person's document.
    /// </remarks>
    [Fact]
    public async Task TestPhantomInstantPressNeverReachesTheEngineOrTheDocument()
    {
        capture.SetDuration(TimeSpan.FromMilliseconds(284));
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        // The release the machine deferred to the end of the double-tap window.
        controller.Stop();
        await Settle();

        Assert.Equal(1, capture.StartCount);
        Assert.Equal(0, transcribeCalls.Count);
        Assert.Empty(inserter.InsertedTexts);
        // Nobody pressed anything, so there is nothing to explain to anybody.
        Assert.Empty(overlay.Notices);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    [Fact]
    public async Task TestLongHoldWithAnEmptyRecordingBlamesTheMicrophone()
    {
        // The key was held for twelve seconds of wall clock, and the capture
        // returned zero: the input is muted, the USB microphone is dead, or
        // another application is holding the device. The guard above is about "I
        // changed my mind", but it only looked at the recorded audio - so a person
        // who dictated a whole paragraph got nothing at all: no text, no message,
        // no sound.
        capture.SetDuration(TimeSpan.Zero);
        var clock = new TestClock();
        var controller = MakeController(clock: clock);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        clock.Advance(TimeSpan.FromSeconds(12));
        controller.Stop();
        await Settle();

        // There is nothing to recognize.
        Assert.Equal(0, transcribeCalls.Count);
        Assert.Empty(inserter.InsertedTexts);
        // Staying silent here is not allowed.
        Assert.Single(overlay.Notices);
        // This is not "changed my mind", this is a broken input.
        Assert.Equal(DictationNoticeKind.Failure, overlay.Notices[0].Kind);
        // There is no point in rescuing an empty recording.
        Assert.Null(overlay.Notices[0].RecoveryAudio);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    // MARK: - Empty result

    [Fact]
    public async Task TestEmptyRecognitionTellsThePersonInsteadOfVanishing()
    {
        // The person held the key and spoke, but the engine heard nothing: the
        // wrong microphone, a noisy room, speech into the table. The panel simply
        // faded - and that is indistinguishable from "the text was inserted
        // somewhere else": the person goes looking for the missing phrase in
        // someone else's window.
        var controller = MakeController(recognized: "   ");

        await RunFullDictation(controller);

        // There is nothing to insert.
        Assert.Empty(inserter.InsertedTexts);
        // Staying silent here is not allowed.
        Assert.Single(overlay.Notices);
        // This is not a failure - the engine simply did not hear.
        Assert.Equal(DictationNoticeKind.Info, overlay.Notices[0].Kind);
        // The voice does not stay on disk.
        Assert.Null(overlay.Notices[0].RecoveryAudio);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    /// <summary>
    /// Silence does not come back from this engine as nothing - it comes back as punctuation.
    /// </summary>
    /// <remarks>
    /// A comma, an ellipsis, a run of dozens of them. Seen from a text field that is a
    /// hallucination, and it landed in a person's document between two dictated phrases: the
    /// emptiness gate waved it through, because a string of ellipses is not an empty string.
    /// </remarks>
    [Theory]
    [InlineData("...")]
    [InlineData("…")]
    [InlineData(",")]
    [InlineData(". . .")]
    [InlineData("…………………")]
    [InlineData(" , … . ")]
    public async Task TestPunctuationOnlyRecognitionTellsThePersonInsteadOfInserting(string invented)
    {
        var controller = MakeController(recognized: invented);

        await RunFullDictation(controller);

        Assert.Empty(inserter.InsertedTexts);
        // The same answer an empty result gets: not a failure, but never silence either.
        Assert.Single(overlay.Notices);
        Assert.Equal(DictationNoticeKind.Info, overlay.Notices[0].Kind);
        Assert.True(overlay.Notices[0].WordsDidNotLand);
        // The voice does not stay on disk.
        Assert.Null(overlay.Notices[0].RecoveryAudio);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    /// <summary>
    /// One letter or one digit is a dictation, and the gate must not eat it.
    /// </summary>
    /// <remarks>
    /// A number dictated into a form is the shortest real dictation there is. Over-filtering
    /// here would lose it exactly as the hallucination loses trust, only more quietly.
    /// </remarks>
    [Theory]
    [InlineData("3", "3")]
    [InlineData("3.14", "3.14")]
    [InlineData("ok", "Ok")]
    [InlineData("да!", "Да!")]
    public async Task TestOneDictatedCharacterIsStillADictation(string recognized, string expected)
    {
        var controller = MakeController(recognized: recognized);

        await RunFullDictation(controller);

        Assert.Equal([expected], inserter.InsertedTexts);
        Assert.Empty(overlay.Notices);
    }

    [Fact]
    public async Task TestEmptyRecognitionAfterCancelStaysSilent()
    {
        // A cancelled dictation needs no explanation: the person closed it themselves.
        var controller = MakeController(recognized: "   ");

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Cancel();
        await Settle(30);

        // After a cancellation there is nothing to report.
        Assert.Empty(overlay.Notices);
    }

    // MARK: - Cancel at insertion time

    [Fact]
    public async Task TestCancelDuringInsertionCannotTakeTheTextBack()
    {
        // Cancel pressed at the moment of insertion comes too late: the event has
        // already gone into someone else's application. What matters is that the
        // session closes properly instead of hanging in "inserting".
        //
        // The moment "insertion is in progress" is caught by polling, not by a
        // fixed sleep: on an overloaded runner a 30 ms sleep lasts two seconds,
        // during which the insertion finishes entirely.
        inserter.SetInsertDelay(TimeSpan.FromMilliseconds(800));
        var controller = MakeController();

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await WaitForState(controller, DictationState.Inserting);
        Assert.Equal(DictationState.Inserting, controller.State);

        controller.Cancel();
        await Settle(40);
        await WaitForState(controller, DictationState.Idle);

        Assert.Equal(["Привет мир"], inserter.InsertedTexts);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    // MARK: - A microphone that is slow to start

    private const string SlowStartMessage = "Starting the microphone took too long. Dictation was stopped.";

    private DictationController MakeSlowStartController(
        SlowOpeningMicrophone microphone,
        FakeSounds sounds,
        TimeSpan? microphoneStartDeadline = null,
        TimeSpan? preparingStopDeadline = null)
    {
        TranscribeCounter counter = transcribeCalls;
        return new DictationController(
            capture: microphone,
            transcribe: (_, _) =>
            {
                counter.Increment();
                return Task.FromResult(new ASRResult(
                    "words",
                    audioDuration: TimeSpan.FromSeconds(2),
                    processingDuration: TimeSpan.FromSeconds(0.1)));
            },
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            preparingStopDeadline: preparingStopDeadline ?? TimeSpan.FromMilliseconds(500),
            microphoneStartDeadline: microphoneStartDeadline ?? TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilIdle(DictationController controller, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(2);
        while (controller.State != DictationState.Idle && deadline.Elapsed < limit)
        {
            await Task.Yield();
            await Task.Delay(2);
        }
    }

    /// <summary>
    /// The key is still held and the microphone has not opened. Waiting for the release to say
    /// so meant a person could talk for many seconds into nothing; they hear about it while they
    /// are still holding the key.
    /// </summary>
    [Fact]
    public async Task TestAMicrophoneThatNeverStartsIsReportedWhileTheKeyIsHeld()
    {
        var gate = new Gate();
        var microphone = new SlowOpeningMicrophone(gate);
        var sounds = new FakeSounds();
        var controller = MakeSlowStartController(
            microphone,
            sounds,
            microphoneStartDeadline: TimeSpan.FromMilliseconds(50));
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        var began = Stopwatch.StartNew();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await WaitUntilIdle(controller);
        TimeSpan ended = began.Elapsed;
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.True(ended >= TimeSpan.FromMilliseconds(50), "not before the deadline");
        Assert.True(ended < TimeSpan.FromSeconds(1), "while the key is still held, not at its release");
        Assert.Equal([SlowStartMessage], notices.Select(notice => notice.Message));
        Assert.Equal(DictationNoticeKind.Failure, notices[0].Kind);
        Assert.True(notices[0].WordsDidNotLand);
        Assert.Equal(1, sounds.AttentionPlays);

        // The microphone comes back; the next press dictates normally.
        gate.Open();
        await Settle();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);
        controller.Cancel();
        await WaitUntilIdle(controller);
    }

    /// <summary>
    /// A microphone that opens inside the deadline is the normal take. A tap released while it
    /// was opening still finishes once it is listening.
    /// </summary>
    [Fact]
    public async Task TestAStartWithinTheDeadlineIsNotReported()
    {
        var gate = new Gate();
        var microphone = new SlowOpeningMicrophone(gate);
        var sounds = new FakeSounds();
        var controller = MakeSlowStartController(
            microphone,
            sounds,
            microphoneStartDeadline: TimeSpan.FromMilliseconds(300));
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Task.Delay(20);
        gate.Open();
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);
        await Task.Delay(400);
        Assert.Equal(DictationState.Listening, controller.State);
        Assert.Empty(notices);
        controller.Cancel();
        await WaitUntilIdle(controller);

        // A tap released while the microphone is still opening.
        var second = new SlowOpeningMicrophone(new Gate());
        var tapController = MakeSlowStartController(second, sounds);
        var tapNotices = new List<DictationNotice>();
        tapController.OnNotice = notice => { lock (tapNotices) { tapNotices.Add(notice); } };
        tapController.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Task.Yield();
        tapController.Stop();
        Assert.True(tapController.State == DictationState.Preparing, "a tap waits for the microphone");
        tapController.Cancel();
        await WaitUntilIdle(tapController);
        Assert.Empty(tapNotices);
    }

    /// <summary>
    /// When the controller sees a tap end. The hotkey holds back the release of any press
    /// shorter than its double-tap window (0.35 s) until that window has passed, so every tap
    /// reaches <c>Stop()</c> about 0.35 s after its key-down, never sooner.
    /// </summary>
    private static readonly TimeSpan TapReleaseDelivery = TimeSpan.FromMilliseconds(360);

    /// <summary>
    /// A key held long enough to have spoken, released before the microphone opened. Nothing was
    /// recorded and nothing can be: finishing when the microphone finally opens recognized an
    /// empty take and dropped it without a word. The person spoke, so the person is told, at
    /// once.
    ///
    /// "Long enough" is the same hold that turns any empty take from a change of mind into a
    /// broken microphone, <see cref="DictationDurationPolicy.MinimumHoldForSilentInput"/>.
    /// </summary>
    [Fact]
    public async Task TestAHeldReleaseBeforeTheMicrophoneOpensIsReportedNotDropped()
    {
        var gate = new Gate();
        var microphone = new SlowOpeningMicrophone(gate);
        var sounds = new FakeSounds();
        var controller = MakeSlowStartController(microphone, sounds);
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Task.Delay(DictationDurationPolicy.MinimumHoldForSilentInput + TimeSpan.FromMilliseconds(100));
        Assert.Equal(DictationState.Preparing, controller.State);
        controller.Stop();
        Assert.True(
            controller.State != DictationState.Preparing,
            "told at the release, not when the microphone opens");
        // Inside the short bound a tap would have waited for.
        await Task.Delay(300);
        gate.Open();
        await WaitUntilIdle(controller);
        await Settle();

        Assert.Equal([SlowStartMessage], notices.Select(notice => notice.Message));
        Assert.Equal(1, sounds.AttentionPlays);
        Assert.Empty(inserter.InsertedTexts);
        Assert.Equal(DictationState.Idle, controller.State);
    }

    /// <summary>
    /// A tap is a change of mind, even when the microphone is slow. Its release arrives at the
    /// end of the double-tap window, and a microphone that opens 0.1 s after that is still
    /// inside the short bound: the empty take ends without a notice and without the attention
    /// sound.
    /// </summary>
    [Fact]
    public async Task TestATapStaysQuietWhenTheMicrophoneOpensJustAfterItsRelease()
    {
        var gate = new Gate();
        var microphone = new SlowOpeningMicrophone(gate);
        var sounds = new FakeSounds();
        var controller = MakeSlowStartController(microphone, sounds);
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Task.Delay(TapReleaseDelivery);
        controller.Stop();
        await Task.Delay(100);
        gate.Open();
        await WaitUntilIdle(controller);
        await Settle();

        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Empty(notices);
        Assert.Equal(0, sounds.AttentionPlays);
        Assert.Empty(inserter.InsertedTexts);
    }

    /// <summary>
    /// A tap is a change of mind, not a lost take: it stays quiet, and a microphone that never
    /// opens still ends it within the short bound after the release rather than the long one
    /// after the key-down.
    /// </summary>
    [Fact]
    public async Task TestATapReleasedBeforeTheMicrophoneOpensKeepsTheShortBound()
    {
        var gate = new Gate();
        var microphone = new SlowOpeningMicrophone(gate);
        var sounds = new FakeSounds();
        var controller = MakeSlowStartController(
            microphone,
            sounds,
            microphoneStartDeadline: TimeSpan.FromSeconds(5),
            preparingStopDeadline: TimeSpan.FromMilliseconds(60));
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Task.Delay(TapReleaseDelivery);
        var released = Stopwatch.StartNew();
        controller.Stop();
        Assert.True(controller.State == DictationState.Preparing, "a tap waits for the microphone");
        // Two seconds rather than upstream's one: still well inside the five-second bound from
        // the key-down, and a loaded test runner is not a microphone that never opened.
        await WaitUntilIdle(controller, TimeSpan.FromSeconds(2));

        Assert.True(controller.State == DictationState.Idle, "the short bound after release still holds");
        Assert.True(released.Elapsed >= TimeSpan.FromMilliseconds(60));
        Assert.Equal([SlowStartMessage], notices.Select(notice => notice.Message));
        gate.Open();
        await Settle();
    }

    /// <summary>
    /// A double tap promotes the take to hands-free while the microphone is still opening.
    /// Nobody will release a key now, so the deadline from the key-down is the only thing that
    /// can end a microphone that never opens.
    /// </summary>
    [Fact]
    public async Task TestAHandsFreeTakeKeepsTheStartDeadline()
    {
        var gate = new Gate();
        var microphone = new SlowOpeningMicrophone(gate);
        var sounds = new FakeSounds();
        var controller = MakeSlowStartController(
            microphone,
            sounds,
            microphoneStartDeadline: TimeSpan.FromMilliseconds(300),
            preparingStopDeadline: TimeSpan.FromMilliseconds(30));
        var notices = new List<DictationNotice>();
        controller.OnNotice = notice => { lock (notices) { notices.Add(notice); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Task.Delay(5);
        // The double tap: the first release, then the second press.
        controller.Stop();
        controller.PromoteToHandsFree();
        await Task.Delay(120);
        Assert.True(
            controller.State == DictationState.Preparing,
            "the release bound belonged to the tap, not to hands-free");
        Assert.Empty(notices);

        await WaitUntilIdle(controller);
        await Settle();
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal([SlowStartMessage], notices.Select(notice => notice.Message));
        Assert.Equal(1, sounds.AttentionPlays);
        gate.Open();
        await Settle();
    }

    // MARK: - State flow

    [Fact]
    public async Task TestStateChangesArriveOnceAndInOrder()
    {
        // The interface is subscribed to the state, and permissions are polled
        // once a second. Repeating the same state would mean redrawing the
        // settings window every second for nothing.
        var controller = MakeController();
        var log = new StateLog();
        controller.OnStateChange = log.Append;

        await RunFullDictation(controller);

        Assert.Equal(
            [
                DictationState.Preparing,
                DictationState.Listening,
                DictationState.Transcribing,
                DictationState.Inserting,
                DictationState.Idle,
            ],
            log.States);
    }
}
