namespace DictationCore.Tests;

/// <summary>
/// Speed report: three marks from the key release.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class DictationSpeedTests
{
    /// <summary>Inserter with a scripted set of marks - they cannot be caught from outside.</summary>
    private sealed class MarkingInserter : ITextInserting
    {
        private readonly Lock gate = new();
        private readonly InsertionMarks marks;
        private readonly List<string> insertedTexts = [];
        private Exception? error;

        public MarkingInserter(InsertionMarks marks) { this.marks = marks; }

        public IReadOnlyList<string> InsertedTexts { get { lock (gate) { return insertedTexts.ToArray(); } } }

        public void SetError(Exception value) { lock (gate) { error = value; } }

        public Task Insert(string text, TargetApplication? target) =>
            InsertReportingMarks(text, target);

        public Task<InsertionMarks> InsertReportingMarks(string text, TargetApplication? target)
        {
            lock (gate)
            {
                if (error is { } failure) { throw failure; }
                insertedTexts.Add(text);
                return Task.FromResult(marks);
            }
        }

        public Task PressReturn() => Task.CompletedTask;

        public TargetApplication? FrontmostApplication() => null;
    }

    /// <summary>Capture with a scripted microphone warm-up.</summary>
    private sealed class TimedCapture : IAudioCapturing
    {
        private readonly Lock gate = new();
        private readonly TimeSpan? latency;
        private string? url;

        public TimedCapture(TimeSpan? latency) { this.latency = latency; }

        public Task<string> StartRecording()
        {
            string path = Path.Combine(Path.GetTempPath(), $"speed-{Guid.NewGuid()}.wav");
            File.WriteAllBytes(path, [0]);
            lock (gate) { url = path; }
            return Task.FromResult(path);
        }

        public Task<(string Url, TimeSpan Duration)> StopRecording()
        {
            lock (gate)
            {
                if (url is not { } path) { throw new AudioCaptureError.NotRecording(); }
                return Task.FromResult((path, TimeSpan.FromSeconds(2)));
            }
        }

        public Task AbortRecording() => Task.CompletedTask;

        public Task<TimeSpan?> StartupLatency() => Task.FromResult(latency);
    }

    /// <summary>A capture whose start waits for the test, like a cold or blocked device.</summary>
    private sealed class DeferredStartCapture : IAudioCapturing
    {
        private readonly Gate gate;
        private readonly string url = Path.Combine(Path.GetTempPath(), $"deferred-speed-{Guid.NewGuid()}.wav");

        public DeferredStartCapture(Gate gate) { this.gate = gate; }

        public async Task<string> StartRecording()
        {
            await gate.Pass();
            File.WriteAllBytes(url, [0]);
            return url;
        }

        public Task<(string Url, TimeSpan Duration)> StopRecording() =>
            Task.FromResult((url, TimeSpan.FromSeconds(2)));

        public Task AbortRecording() => Task.CompletedTask;
    }

    private static async Task Settle(int iterations = 20)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    /// <summary>A clock that hands out known moments in turn.</summary>
    private sealed class ScriptedClock
    {
        private readonly Lock gate = new();
        private readonly MonotonicInstant origin = MonotonicInstant.Now;
        private readonly TimeSpan[] offsets;
        private int index;

        public ScriptedClock(params TimeSpan[] offsets) { this.offsets = offsets; }

        public MonotonicInstant Next()
        {
            lock (gate)
            {
                TimeSpan offset = index < offsets.Length
                    ? offsets[index]
                    : offsets.Length == 0 ? TimeSpan.Zero : offsets[^1];
                index += 1;
                return origin.Advanced(offset);
            }
        }

        public MonotonicInstant Instant(TimeSpan offset) => origin.Advanced(offset);
    }

    private sealed class WallClockBox
    {
        private readonly Lock gate = new();
        private DateTimeOffset value = DateTimeOffset.Now;

        public DateTimeOffset Current { get { lock (gate) { return value; } } }

        public void RewindAnHour() { lock (gate) { value = value.AddHours(-1); } }
    }

    [Fact]
    public async Task TestSpeedIsMeasuredFromTheKeyRelease()
    {
        // t0 = 0 ms (release), t1 = 120 ms (the engine returned the text).
        var clock = new ScriptedClock(TimeSpan.Zero, TimeSpan.FromMilliseconds(120));
        var inserter = new MarkingInserter(new InsertionMarks(
            PasteDispatchedAt: clock.Instant(TimeSpan.FromMilliseconds(150)),
            ClipboardRestoredAt: clock.Instant(TimeSpan.FromMilliseconds(1150))));
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new TimedCapture(latency: TimeSpan.FromMilliseconds(130)),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет мир",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            monotonicNow: clock.Next);
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(report);
        Assert.Equal(TimeSpan.FromMilliseconds(120), report.ToRecognizedText);
        Assert.Equal(TimeSpan.FromMilliseconds(150), report.ToPasteDispatched);
        Assert.Equal(TimeSpan.FromMilliseconds(1150), report.ToClipboardRestored);
        Assert.Equal(TimeSpan.FromMilliseconds(130), report.MicrophoneStartup);
    }

    [Fact]
    public async Task TestReleaseDuringPreparingIsIncludedInStopToTextMetric()
    {
        var gate = new Gate();
        var inserter = new MarkingInserter(new InsertionMarks());
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new DeferredStartCapture(gate),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "measured",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.Zero)),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle(2);
        controller.Stop();
        await Task.Delay(80);
        gate.Open();
        await Settle(30);

        Assert.NotNull(report);
        Assert.True(
            report.ToRecognizedText >= TimeSpan.FromMilliseconds(70),
            "a cold/blocked capture start after key release must not be hidden from telemetry");
    }

    /// <summary>The marks are in order: text → insertion → clipboard restoration.</summary>
    [Fact]
    public async Task TestMarksAreOrdered()
    {
        var clock = new ScriptedClock(TimeSpan.Zero, TimeSpan.FromMilliseconds(120));
        var inserter = new MarkingInserter(new InsertionMarks(
            PasteDispatchedAt: clock.Instant(TimeSpan.FromMilliseconds(150)),
            ClipboardRestoredAt: clock.Instant(TimeSpan.FromMilliseconds(1150))));
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new TimedCapture(latency: null),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            monotonicNow: clock.Next);
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(report);
        Assert.NotNull(report.ToPasteDispatched);
        Assert.NotNull(report.ToClipboardRestored);
        Assert.True(report.ToRecognizedText <= report.ToPasteDispatched);
        Assert.True(report.ToPasteDispatched <= report.ToClipboardRestored);
    }

    /// <summary>
    /// Wall clocks do not affect the report at all.
    ///
    /// This is why the monotonic clock is a separate dependency: sleep, daylight
    /// saving or an NTP step in the middle of a dictation would otherwise turn
    /// "stop → text" negative, that is, into a lie.
    /// </summary>
    [Fact]
    public async Task TestReportIgnoresWallClockJumps()
    {
        var clock = new ScriptedClock(TimeSpan.Zero, TimeSpan.FromMilliseconds(120));
        var wall = new WallClockBox();
        var inserter = new MarkingInserter(new InsertionMarks());
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new TimedCapture(latency: null),
            transcribe: (_, _) =>
            {
                // While recognition is in progress, the clock is set back an hour.
                wall.RewindAnHour();
                return Task.FromResult(new ASRResult(
                    "привет",
                    audioDuration: TimeSpan.FromSeconds(2),
                    processingDuration: TimeSpan.FromSeconds(0.1)));
            },
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            now: () => wall.Current,
            monotonicNow: clock.Next);
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(report);
        Assert.Equal(TimeSpan.FromMilliseconds(120), report.ToRecognizedText);
    }

    /// <summary>The insertion failed - there is no honest number, so no report.</summary>
    [Fact]
    public async Task TestFailedInsertionProducesNoReport()
    {
        var inserter = new MarkingInserter(new InsertionMarks());
        inserter.SetError(new TextInsertionError.SecureInputActive());
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new TimedCapture(latency: null),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Null(report);
    }

    /// <summary>An edge without a measurement returns <c>null</c>, not zero: it is not instant.</summary>
    [Fact]
    public async Task TestUnmeasuredEdgesReportNilNotZero()
    {
        var inserter = new MarkingInserter(new InsertionMarks());
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new TimedCapture(latency: null),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "привет",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds());
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(report);
        Assert.Null(report.ToPasteDispatched);
        Assert.Null(report.ToClipboardRestored);
        Assert.Null(report.MicrophoneStartup);
    }

    /// <summary>The default implementation does not pretend to measure and inserts exactly once.</summary>
    [Fact]
    public async Task TestDefaultInsertReportingMarksCallsInsertExactlyOnce()
    {
        var plain = new PlainInserter();
        var marks = await ((ITextInserting)plain).InsertReportingMarks("привет", target: null);
        Assert.Null(marks.PasteDispatchedAt);
        Assert.Null(marks.ClipboardRestoredAt);
        Assert.Equal(1, plain.InsertCount);
    }

    private sealed class PlainInserter : ITextInserting
    {
        private int insertCount;

        public int InsertCount => Volatile.Read(ref insertCount);

        public Task Insert(string text, TargetApplication? target)
        {
            Interlocked.Increment(ref insertCount);
            return Task.CompletedTask;
        }

        public Task PressReturn() => Task.CompletedTask;

        public TargetApplication? FrontmostApplication() => null;
    }
}

// MARK: - Phase breakdown

/// <summary>
/// One "stop → text" number cannot say which stage owned the wait, and the three stages fail for
/// entirely different reasons. These tests pin the attribution, because a diagnosis built on a
/// mislabelled stage is worse than no diagnosis.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class DictationPhaseBreakdownTests
{
    private static async Task Settle()
    {
        for (int i = 0; i < 40; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    /// <summary>A clock that hands out known moments in turn, and the last one forever after.</summary>
    private sealed class ScriptedClock
    {
        private readonly Lock gate = new();
        private readonly MonotonicInstant origin = MonotonicInstant.Now;
        private readonly TimeSpan[] offsets;
        private int index;

        public ScriptedClock(params TimeSpan[] offsets) { this.offsets = offsets; }

        public MonotonicInstant Next()
        {
            lock (gate)
            {
                TimeSpan offset = index < offsets.Length ? offsets[index] : offsets[^1];
                index += 1;
                return origin.Advanced(offset);
            }
        }
    }

    /// <summary>
    /// A cold engine is not a slow engine. The stage that waited for the model must carry the
    /// seconds, and recognition must keep only its own.
    /// </summary>
    [Fact]
    public async Task TestPreparationWaitIsAttributedToPreparationNotToRecognition()
    {
        // stop = 0 ms, capture frozen = 40 ms, model ready = 8040 ms, text returned = 8100 ms.
        var clock = new ScriptedClock(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(8040),
            TimeSpan.FromMilliseconds(8100));
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.05))),
            inserter: new FakeInserter(),
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            monotonicNow: clock.Next,
            prepareForTranscription: _ => Task.CompletedTask);
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(report);
        DictationPhaseBreakdown? phases = report.Phases;
        Assert.NotNull(phases);
        Assert.Equal(TimeSpan.FromMilliseconds(40), phases.CaptureFreeze);
        Assert.Equal(TimeSpan.FromMilliseconds(8000), phases.EnginePreparation);
        Assert.Equal(TimeSpan.FromMilliseconds(60), phases.Recognition);
        Assert.Equal(TimeSpan.FromMilliseconds(50), phases.EngineProcessing);
        Assert.Equal(TimeSpan.FromSeconds(2), phases.AudioDuration);
    }

    /// <summary>
    /// Without a preparation hook there is no preparation stage to report, and recognition owns
    /// everything after the freeze. Absent, never zero: a zero would claim a stage ran instantly.
    /// </summary>
    [Fact]
    public async Task TestNoPreparationHookLeavesTheStageAbsentAndRecognitionWhole()
    {
        var clock = new ScriptedClock(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(230));
        DictationSpeedReport? report = null;
        var controller = new DictationController(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.2))),
            inserter: new FakeInserter(),
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            monotonicNow: clock.Next);
        controller.OnSpeed = value => report = value;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(report);
        DictationPhaseBreakdown? phases = report.Phases;
        Assert.NotNull(phases);
        Assert.Null(phases.EnginePreparation);
        Assert.Equal(TimeSpan.FromMilliseconds(30), phases.CaptureFreeze);
        Assert.Equal(TimeSpan.FromMilliseconds(200), phases.Recognition);
    }
}

// MARK: - Waiting for the model

/// <summary>
/// The panel is the only feedback channel during dictation. A stop-time model load spends its
/// whole length in the transcribing state, so the controller has to say which of the two is
/// happening - but only when the distinction is visible to a person, never as a flicker on the
/// warm path.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class EnginePreparationWaitTests
{
    private static async Task Settle(int rounds = 60)
    {
        for (int i = 0; i < rounds; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    private static DictationController MakeController(
        Func<CancellationToken, Task> prepare,
        TimeSpan noticeDelay) =>
        new(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "words",
                audioDuration: TimeSpan.FromSeconds(1),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new FakeInserter(),
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            prepareForTranscription: prepare,
            enginePreparationNoticeDelay: noticeDelay);

    /// <summary>A load long enough to be noticed is announced, and retracted once the model is there.</summary>
    [Fact]
    public async Task TestALongModelWaitIsAnnouncedAndThenRetracted()
    {
        List<bool> announcements = [];
        var controller = MakeController(
            prepare: token => Task.Delay(220, token),
            noticeDelay: TimeSpan.FromMilliseconds(20));
        controller.OnEnginePreparationWait = value => { lock (announcements) { announcements.Add(value); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Equal([true, false], announcements);
    }

    /// <summary>
    /// A resident engine answers immediately. Flipping the panel for every take would be a
    /// flicker on the path almost every dictation takes.
    /// </summary>
    [Fact]
    public async Task TestAWarmEngineNeverMentionsWaiting()
    {
        List<bool> announcements = [];
        var controller = MakeController(
            prepare: _ => Task.CompletedTask,
            noticeDelay: TimeSpan.FromMilliseconds(400));
        controller.OnEnginePreparationWait = value => { lock (announcements) { announcements.Add(value); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Empty(announcements);
    }

    /// <summary>
    /// A load that fails still has to take its message back. Leaving "Waking the model…" on
    /// screen would outlive the take that owns it.
    /// </summary>
    [Fact]
    public async Task TestAFailedPreparationRetractsItsAnnouncement()
    {
        List<bool> announcements = [];
        var controller = MakeController(
            prepare: async token =>
            {
                await Task.Delay(60, token);
                throw new InvalidOperationException("preparation failed");
            },
            noticeDelay: TimeSpan.FromMilliseconds(10));
        controller.OnEnginePreparationWait = value => { lock (announcements) { announcements.Add(value); } };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.True(announcements.Count > 0 && !announcements[^1], "the panel never keeps a stale wait");
        Assert.Equal(1, announcements.Count(value => value));
    }
}
