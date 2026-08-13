namespace DictationCore;

/// <summary>What to do with recognized text if it was not possible to insert it.</summary>
public sealed record RecoveredDictation(string Text);

/// <summary>
/// Dictation core.
///
/// Holds the state of the session and carries it through from keypress to text insertion.
/// Knows nothing about the desktop shell, nor about the microphone, nor about the model - all
/// this comes from outside via interfaces, so it is tested here.
///
/// Invariants, without which the product breaks on the very first users, are
/// collected in separate checks along the code: each of them came from a real
/// bug in the previous product.
/// </summary>
/// <remarks>
/// <para>
/// The Swift original is a <c>@MainActor final class</c>: every synchronous
/// stretch of code runs to completion without interleaving, and the actor is
/// released at every <c>await</c>. That is modelled here by one
/// <see cref="Lock"/> that guards the session state and is never held across an
/// await - the C# <c>lock</c> statement makes the second half a compile-time
/// guarantee. Holding a lock across the engine, the pipeline or the inserter
/// would be a different machine altogether: <see cref="Cancel"/> arriving while
/// recognition is wedged would then wait for the wedge instead of ending the
/// session, which is exactly the hang the deadline exists to prevent.
/// </para>
/// <para>
/// The subscriber callbacks are invoked under the same lock, because on the
/// main actor they were serialized with everything else. A stale session's tail
/// and a live gesture can therefore never interleave inside one notification.
/// </para>
/// <para>
/// The Swift initializer takes <c>transcribeSamples</c> third; C# requires
/// optional parameters to follow the required ones, so it moved down. Every
/// call site names its arguments, exactly as Swift's labels forced it to.
/// </para>
/// </remarks>
public sealed class DictationController
{
    // MARK: - Observable state

    private readonly Lock gate = new();

    private DictationState state = DictationState.Idle;

    public DictationState State { get { lock (gate) { return state; } } }

    /// <summary>
    /// Equality is checked intentionally: the interface is subscribed to
    /// changes, and permissions are polled once per second. Without this check
    /// the window settings would be redrawn every second.
    /// </summary>
    private void SetState(DictationState value)
    {
        if (state == value) { return; }

        state = value;
        OnStateChange?.Invoke(value);
    }

    private RecoveredDictation? pendingRecovery;

    public RecoveredDictation? PendingRecovery { get { lock (gate) { return pendingRecovery; } } }

    public Action<DictationState>? OnStateChange { get; set; }

    public Action<DictationNotice>? OnNotice { get; set; }

    /// <summary>
    /// Successful insertion - with text that actually went into the application.
    /// The "correct the last dictation" window needs the text; it does not get to disk.
    /// </summary>
    public Action<string>? OnTextInserted { get; set; }

    /// <summary>
    /// The origin of the dictation just inserted: what the text was and what it has become.
    ///
    /// A separate callback rather than an extension of <see cref="OnTextInserted"/>:
    /// that one is pulled by a set of tests that know nothing about the origin,
    /// and changing its signature would have to happen in all of them. An
    /// additive optional callback does not touch them.
    /// </summary>
    public Action<PipelineProvenance>? OnDictationCompleted { get; set; }

    /// <summary>
    /// How long "release → text in place" took. For successful insertions only:
    /// an unsuccessful one has no honest number.
    /// </summary>
    public Action<DictationSpeedReport>? OnSpeed { get; set; }

    /// <summary>
    /// Tells whether recording goes on without holding: this decides how to
    /// interpret the next keystroke.
    /// </summary>
    public Action<bool>? OnHandsFreeChange { get; set; }

    /// <summary>
    /// Recognition exceeded its deadline - the engine is presumed wedged on a
    /// dead system service. The owner should recycle it so the next dictation
    /// runs on a fresh session instead of the same stuck one.
    /// </summary>
    public Action? OnTranscriptionStall { get; set; }

    private bool isHandsFreeActive;

    /// <summary>Whether recording occurs without holding down a key.</summary>
    public bool IsHandsFreeActive { get { lock (gate) { return isHandsFreeActive; } } }

    private void SetHandsFreeActive(bool value)
    {
        if (isHandsFreeActive == value) { return; }

        isHandsFreeActive = value;
        OnHandsFreeChange?.Invoke(value);
    }

    // MARK: - Dependencies

    private readonly IAudioCapturing capture;
    private readonly Func<string, CancellationToken, Task<ASRResult>> transcribe;

    /// <summary>
    /// Fast path for captures that already own recognizer-ready PCM. The file
    /// delegate remains the compatibility and recovery path.
    /// </summary>
    private readonly Func<float[], CancellationToken, Task<ASRResult>>? transcribeSamples;

    private readonly ITextInserting inserter;
    private readonly IOverlayPresenting overlay;
    private readonly ISounding sounds;
    private readonly IRecordingRecoveryStoring recordingRecovery;
    private readonly Func<TextPipeline> pipeline;

    /// <summary>Session hours. A separate dependency for exactly the same reason as the microphone.</summary>
    private readonly Func<DateTimeOffset> now;

    /// <summary>
    /// The monotonic clock is separate from the wall clock, and this is not duplication.
    ///
    /// <see cref="now"/> supplies wall time where a human-readable moment is
    /// needed. Wall clocks are not suitable for speeds at all: sleep, daylight
    /// saving or an NTP step in the middle of a dictation would give "-400 ms",
    /// that is, simply a lie. The capture and the inserter already live on the
    /// monotonic clock, so all the marks are comparable with each other.
    /// </summary>
    private readonly Func<MonotonicInstant> monotonicNow;

    /// <summary>
    /// How long recognition may run for a recording of a given duration.
    /// Injectable so tests do not wait twenty real seconds for a stall.
    /// </summary>
    private readonly Func<TimeSpan, TimeSpan> transcriptionDeadline;

    /// <summary>
    /// Bring the engine into memory before recognition, when the owner manages
    /// residency. Separate from <see cref="transcribe"/> so a cold reload gets
    /// its own generous budget instead of eating the recognition deadline: a
    /// load that overlaps the person's speech must not count against the
    /// transcription of a two-second utterance.
    /// </summary>
    private readonly Func<CancellationToken, Task>? prepareForTranscription;

    /// <summary>
    /// The reload budget. Covers a cache-purged model compile; a breach means
    /// the engine is wedged and flows into the same stall recovery.
    /// </summary>
    private readonly TimeSpan prepareDeadline;

    // MARK: - Session state

    /// <summary>
    /// Application into which we will insert the text.
    ///
    /// Taken at the moment the key is pressed, and not at the end: while
    /// recognition is running the focus could go away, but the text must get to
    /// where it was dictated.
    /// </summary>
    private TargetApplication? targetApplication;

    /// <summary>
    /// A key release that occurred before the recording began.
    ///
    /// Resets in exactly three places: at the start of the session, at its
    /// cancellation, and in the final cleaning. A lost flag leaves recording enabled.
    /// </summary>
    private bool deferredStopRequested;

    private bool cancellationRequested;
    private bool isHandsFree;

    /// <summary>
    /// The Swift original keeps the finalization <c>Task</c> to cancel it; here
    /// the cancellation source alone carries that meaning, and its presence is
    /// the "finalization is running" flag.
    /// </summary>
    /// <remarks>
    /// Deliberately never disposed: cancellation registrations of an abandoned
    /// transcription outlive the session that started it.
    /// </remarks>
    private CancellationTokenSource? finalization;

    private DateTimeOffset? recordingStartedAt;

    /// <summary>The moment the key is released is the start of the "stop → text" countdown.</summary>
    private MonotonicInstant? stopRequestedAt;

    /// <summary>
    /// The number of the current session increases at each start.
    ///
    /// Cancellation does not interrupt an already started wait, but only marks
    /// it: recognition finishes reading its buffer and wakes up later - when the
    /// person has managed to start the next dictation. Without a number, such a
    /// tail would carry the cleaning through to the end and put out SOMEONE
    /// ELSE'S live session: the state would show "free" while the microphone
    /// stayed on, with no way out of it.
    /// </summary>
    private int currentSession;

    /// <summary>Whether this session has already asked for the person's attention.</summary>
    private bool hasSoundedThisSession;

    public DictationController(
        IAudioCapturing capture,
        Func<string, CancellationToken, Task<ASRResult>> transcribe,
        ITextInserting inserter,
        IOverlayPresenting overlay,
        ISounding sounds,
        Func<float[], CancellationToken, Task<ASRResult>>? transcribeSamples = null,
        IRecordingRecoveryStoring? recordingRecovery = null,
        Func<TextPipeline>? pipeline = null,
        Func<DateTimeOffset>? now = null,
        Func<MonotonicInstant>? monotonicNow = null,
        Func<TimeSpan, TimeSpan>? transcriptionDeadline = null,
        Func<CancellationToken, Task>? prepareForTranscription = null,
        TimeSpan? prepareDeadline = null)
    {
        this.capture = capture;
        this.transcribe = transcribe;
        this.transcribeSamples = transcribeSamples;
        this.inserter = inserter;
        this.overlay = overlay;
        this.sounds = sounds;
        this.recordingRecovery = recordingRecovery ?? new DiscardingRecordingRecovery();
        this.pipeline = pipeline ?? (static () => new TextPipeline());
        this.now = now ?? (static () => DateTimeOffset.Now);
        this.monotonicNow = monotonicNow ?? (static () => MonotonicInstant.Now);
        this.transcriptionDeadline = transcriptionDeadline ?? TranscriptionDeadline.Deadline;
        this.prepareForTranscription = prepareForTranscription;
        this.prepareDeadline = prepareDeadline ?? TimeSpan.FromSeconds(90);
    }

    // MARK: - Beginning

    /// <summary>Hotkey pressed.</summary>
    public void Begin(bool handsFree, bool isEnabled, bool isModelReady)
    {
        int session;
        lock (gate)
        {
            // The first check is synchronous, before any waiting. Between it and
            // the asynchronous start, a second press can arrive.
            if (!DictationStopPolicy.CanStart(state, isEnabled, isModelReady)) { return; }

            currentSession += 1;
            session = currentSession;
            // Do not touch the previous Copy/Retry: the new recording can be
            // cancelled or fail with an error. Saved text is deleted only by an
            // explicit action or after a successful Retry.
            isHandsFree = handsFree;
            SetHandsFreeActive(handsFree);
            cancellationRequested = false;
            deferredStopRequested = false;
            // We remember the target right away: the focus will go away later.
            targetApplication = inserter.FrontmostApplication();
            SetState(DictationState.Preparing);
        }

        _ = Task.Run(async () =>
        {
            // A capture device can take a noticeable fraction of a second to wake up.
            // Acknowledge the hotkey before that wait so the gesture never feels lost.
            await overlay.Present(DictationState.Preparing, TimeSpan.Zero).ConfigureAwait(false);
            await StartCapture(session).ConfigureAwait(false);
        });
    }

    private async Task StartCapture(int session)
    {
        // Second check of the same condition: between the synchronous part and
        // this line a transition between tasks has happened, during which the
        // session could be cancelled.
        if (!IsPreparingCurrentSession(session))
        {
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }

        try
        {
            await capture.StartRecording().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await Fail(session, new DictationError.Capture(error.ToString())).ConfigureAwait(false);
            return;
        }

        // After waiting, the status is checked again - the cancellation could
        // have come exactly at the moment the engine starts.
        if (!IsPreparingCurrentSession(session))
        {
            // The recording has started, but the session is no longer there. We
            // turn off our microphone - otherwise it stays on and there is
            // nothing left to stop it - but we do not do the cleaning: it
            // belongs to the session that is going on now.
            await capture.AbortRecording().ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }

        lock (gate)
        {
            recordingStartedAt = now();
            SetState(DictationState.Listening);
        }

        await overlay.Present(DictationState.Listening, TimeSpan.Zero).ConfigureAwait(false);

        // The release that came while the engine was rising is processed here -
        // exactly once.
        //
        // Waiting for the first recorded frame used to follow, as the gate for
        // the start sound. The sound is gone (ISounding), and with it the wait:
        // the panel's "listening" promise above is deliberately not frame-gated,
        // because a silent device may never deliver a frame and the person must
        // still be able to stop the session.
        bool deferred;
        lock (gate)
        {
            deferred = deferredStopRequested;
            if (deferred) { deferredStopRequested = false; }
        }

        if (deferred) { Finish(); }
    }

    private bool IsPreparingCurrentSession(int session)
    {
        lock (gate)
        {
            return IsCurrent(session) && state == DictationState.Preparing && !cancellationRequested;
        }
    }

    // MARK: - Stop

    /// <summary>The hotkey is released (or pressed a second time in hands-free mode).</summary>
    public void Stop()
    {
        DeferredStopDecision decision;
        lock (gate)
        {
            decision = DictationStopPolicy.DecideStop(state, isHandsFree);
            if (decision == DeferredStopDecision.DeferUntilListening) { deferredStopRequested = true; }
        }

        if (decision == DeferredStopDecision.StopNow) { Finish(); }
    }

    /// <summary>Stop in the mode without holding - by pressing the key a second time.</summary>
    public void StopHandsFree()
    {
        bool stopNow = false;
        lock (gate)
        {
            if (!isHandsFree) { return; }

            switch (state)
            {
                case DictationState.Listening:
                    stopNow = true;
                    break;
                case DictationState.Preparing:
                    // Same as releasing the key, only pressing: the second press
                    // arrived before the engine rose. Losing it is not allowed -
                    // in this mode the key is not held down and nothing else
                    // stops the recording: the microphone would stay on with no
                    // one listening.
                    deferredStopRequested = true;
                    break;
                case DictationState.Idle:
                case DictationState.Transcribing:
                case DictationState.Inserting:
                    break;
            }
        }

        if (stopNow) { Finish(); }
    }

    /// <summary>
    /// Switch the ongoing session to the mode without holding.
    ///
    /// The double tap comes after the first one has launched the session, and
    /// the first release has tried to finish it. Starting a new session at this
    /// moment is impossible - it would not pass the check for a free state, and
    /// the mode would remain unreachable. Therefore we switch the current one.
    ///
    /// The deferred release is reset: it belonged to the previous gesture, and
    /// in the new mode the key is supposed to be released.
    /// </summary>
    public void PromoteToHandsFree()
    {
        lock (gate)
        {
            if (state != DictationState.Preparing && state != DictationState.Listening) { return; }

            isHandsFree = true;
            SetHandsFreeActive(true);
            deferredStopRequested = false;
        }
    }

    private void Finish()
    {
        int session;
        CancellationToken token;
        lock (gate)
        {
            // Finalization runs exactly once: otherwise the text will be inserted twice.
            if (finalization is not null || state != DictationState.Listening) { return; }

            // One entry for all the stop paths: Stop, StopHandsFree and the
            // deferred release - all of them come here.
            stopRequestedAt = monotonicNow();
            session = currentSession;
            SetState(DictationState.Transcribing);
            finalization = new CancellationTokenSource();
            token = finalization.Token;
        }

        _ = Task.Run(async () =>
        {
            await Finalize(session, token).ConfigureAwait(false);
            ForgetFinalization(session);
        });
    }

    /// <summary>Forget the completed task - but only if it is still our session.</summary>
    private void ForgetFinalization(int session)
    {
        lock (gate)
        {
            if (!IsCurrent(session)) { return; }

            finalization = null;
        }
    }

    /// <summary>Move the take into safekeeping and word the outcome for the notice.</summary>
    private async Task<(string? Saved, string Suffix)> PreserveForRetry(string url)
    {
        try
        {
            string? saved = await recordingRecovery.Preserve(url).ConfigureAwait(false);
            return (
                saved,
                saved is null
                    ? " The recording couldn't be kept."
                    : " The recording is kept on this PC for a few days"
                        + " (Settings → Privacy → Open recordings folder).");
        }
        catch (Exception error)
        {
            return (null, $" The recording is still on disk, but safekeeping failed: {error.Message}");
        }
    }

    private async Task Finalize(int session, CancellationToken token)
    {
        TimeSpan elapsed;
        lock (gate) { elapsed = ElapsedSeconds(); }

        await overlay.Present(DictationState.Transcribing, elapsed).ConfigureAwait(false);

        (string Url, TimeSpan Duration) recording;
        try
        {
            recording = await capture.StopRecording().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await Fail(session, new DictationError.Capture(error.ToString())).ConfigureAwait(false);
            return;
        }

        // Move the in-memory buffer out immediately so every early-return path
        // releases it. The on-disk take remains available for retry/recovery.
        float[]? bufferedSamples = await capture.TakeBufferedSamples().ConfigureAwait(false);

        if (!ShouldContinue(session, token))
        {
            await Discard(recording.Url).ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }

        // Pressed and immediately released - nothing to recognize. The engine
        // refuses to work with such recordings, but showing an error because of
        // it is wrong: the person simply changed their mind.
        //
        // The recorded audio alone does not tell these two stories apart. The key,
        // held for a dozen seconds against a muted, dead or occupied microphone,
        // gives exactly the same empty recording - and being silent about it means
        // eating up an entire dictated paragraph without a single word of
        // explanation. Therefore, the hold is asked here, and this is the only
        // place where the session needs it.
        if (!DictationDurationPolicy.IsWorthTranscribing(recording.Duration))
        {
            TimeSpan held;
            lock (gate) { held = ElapsedSeconds(); }

            DictationDurationPolicy.ShortRecordingOutcome outcome =
                DictationDurationPolicy.OutcomeForShortRecording(held);
            await Discard(recording.Url).ConfigureAwait(false);
            switch (outcome)
            {
                case DictationDurationPolicy.ShortRecordingOutcome.DropSilently:
                    await FinishWithoutInsertion(session).ConfigureAwait(false);
                    break;
                case DictationDurationPolicy.ShortRecordingOutcome.ReportSilentInput:
                    await Report(new DictationNotice(
                        kind: DictationNoticeKind.Failure,
                        message: "The microphone recorded nothing — check that the right input device is selected and not muted."))
                        .ConfigureAwait(false);
                    await Cleanup(session).ConfigureAwait(false);
                    break;
            }

            return;
        }

        ASRResult recognized;
        try
        {
            // A residency-managed engine may be cold here; the reload has been
            // running under the person's voice since the keypress and gets its
            // own budget. Only after the engine is in memory does the
            // recognition deadline start counting.
            if (prepareForTranscription is { } prepare)
            {
                await TranscriptionDeadline.WithTranscriptionDeadline(
                    prepareDeadline,
                    async workToken =>
                    {
                        await prepare(workToken).ConfigureAwait(false);
                        return true;
                    },
                    token).ConfigureAwait(false);
            }

            // The deadline stands between the person and a wedged engine: an
            // inference call stuck on a dead system service ignores cancellation
            // and would hold "Transcribing…" forever.
            recognized = await TranscriptionDeadline.WithTranscriptionDeadline(
                transcriptionDeadline(recording.Duration),
                workToken =>
                    transcribeSamples is { } samples && bufferedSamples is { Length: > 0 }
                        ? samples(bufferedSamples, workToken)
                        : transcribe(recording.Url, workToken),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Discard(recording.Url).ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }
        catch (ASREngineError.Cancelled)
        {
            // Cancellation received through the engine: this is not a failure, nothing to report.
            await Discard(recording.Url).ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }
        catch (TranscriptionTimeout)
        {
            if (!ShouldContinue(session, token))
            {
                await Discard(recording.Url).ConfigureAwait(false);
                await FinishWithoutInsertion(session).ConfigureAwait(false);
                return;
            }

            (string? saved, string suffix) = await PreserveForRetry(recording.Url).ConfigureAwait(false);
            await Report(new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: "Transcribing took too long and was stopped." + suffix,
                recoveryAudio: saved)).ConfigureAwait(false);
            // After the report, so the owner recycles a quiet engine, not one
            // still being blamed for the current session.
            lock (gate) { OnTranscriptionStall?.Invoke(); }

            await Cleanup(session).ConfigureAwait(false);
            return;
        }
        catch (Exception error)
        {
            // A cancellation that came while the engine was running is more
            // important than its failure. Otherwise Escape would leave exactly
            // the trace it gets rid of: the voice and the failure message saved
            // for Retry, on top of the session the person started next.
            if (!ShouldContinue(session, token))
            {
                await Discard(recording.Url).ConfigureAwait(false);
                await FinishWithoutInsertion(session).ConfigureAwait(false);
                return;
            }

            (string? saved, string suffix) = await PreserveForRetry(recording.Url).ConfigureAwait(false);
            await Report(new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: new DictationError.Recognition(error.ToString()).UserMessage + suffix,
                recoveryAudio: saved)).ConfigureAwait(false);
            await Cleanup(session).ConfigureAwait(false);
            return;
        }

        // Check after each wait: while recognition was in progress, the user
        // could press cancel.
        if (!ShouldContinue(session, token))
        {
            await Discard(recording.Url).ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }

        // t1: the engine returned the text. Taken before all the checks below, so
        // that the number does not include our own branching.
        MonotonicInstant recognizedAt = monotonicNow();
        TimeSpan? microphoneStartup = await capture.StartupLatency().ConfigureAwait(false);

        TextPipeline.RunResult run = pipeline().Run(recognized.Text);
        TextPipeline.Output processed = run.Output;
        if (processed.Text.Length == 0)
        {
            // An empty result is not an error: the person could have stayed
            // silent, spoken too quietly or used the wrong microphone. But one
            // cannot stay silent in response: a blank panel without text is
            // indistinguishable from "inserted in the wrong place", and the
            // person goes looking for the missing phrase in someone else's
            // window. A press that is too short does not get here - it is
            // filtered out above and needs no explanation.
            await Discard(recording.Url).ConfigureAwait(false);
            await Report(new DictationNotice(
                kind: DictationNoticeKind.Info,
                message: "Nothing was recognized — nothing was inserted.",
                wordsDidNotLand: true)).ConfigureAwait(false);
            await Cleanup(session).ConfigureAwait(false);
            return;
        }

        await Insert(
            processed,
            run.Provenance,
            recognizedAt,
            microphoneStartup,
            session,
            token).ConfigureAwait(false);
        await Discard(recording.Url).ConfigureAwait(false);
    }

    /// <summary>
    /// Collect a speed report and send it outside.
    ///
    /// Without the release mark there is no report at all: there is nothing to
    /// count "stop → text" from, and showing a number computed from something
    /// else is a lie.
    /// </summary>
    private void ReportSpeed(
        MonotonicInstant recognizedAt,
        InsertionMarks marks,
        TimeSpan? microphoneStartup)
    {
        if (OnSpeed is not { } onSpeed || stopRequestedAt is not { } releasedAt) { return; }

        onSpeed(new DictationSpeedReport(
            releasedAt.DurationTo(recognizedAt),
            marks.PasteDispatchedAt is { } paste ? releasedAt.DurationTo(paste) : null,
            marks.ClipboardRestoredAt is { } restored ? releasedAt.DurationTo(restored) : null,
            microphoneStartup));
    }

    private async Task Insert(
        TextPipeline.Output output,
        PipelineProvenance provenance,
        MonotonicInstant recognizedAt,
        TimeSpan? microphoneStartup,
        int session,
        CancellationToken token)
    {
        lock (gate) { SetState(DictationState.Inserting); }

        // Cmd+V is the meaningful completion feedback. Do not leave a redundant
        // "Inserting" panel over the destination application while the clipboard
        // is restored in the background.
        await overlay.Dismiss().ConfigureAwait(false);

        // Last point where cancellation is still possible. Then the event goes
        // into someone else's application and does not respond.
        if (!ShouldContinue(session, token))
        {
            await FinishWithoutInsertion(session).ConfigureAwait(false);
            return;
        }

        TargetApplication? target;
        lock (gate) { target = targetApplication; }

        InsertionMarks marks;
        try
        {
            marks = await inserter.InsertReportingMarks(output.Text, target).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await HandleInsertionFailure(error, output.Text, session).ConfigureAwait(false);
            return;
        }

        lock (gate)
        {
            OnTextInserted?.Invoke(output.Text);
            // Origin - only after a successful insertion: an unsuccessful one has
            // no "what the person saw" and nothing to give to "copy verbatim".
            OnDictationCompleted?.Invoke(provenance);
            ReportSpeed(recognizedAt, marks, microphoneStartup);
        }

        // The keypress is handled separately from the insertion intentionally.
        // These are different system calls, and the second one fails while the
        // first one is alive - for example, when the user never released the modifier.
        if (output.Command == TrailingCommand.PressReturn)
        {
            try
            {
                await inserter.PressReturn().ConfigureAwait(false);
            }
            catch (Exception)
            {
                await ReportReturnFailure(session).ConfigureAwait(false);
                return;
            }
        }

        await Cleanup(session).ConfigureAwait(false);
    }

    /// <summary>
    /// The text was inserted, but pressing Return failed.
    ///
    /// The general failure path would lie twice here: it would say "the text is
    /// not inserted" when it is in place, and would save a second copy of the
    /// dictation to disk. A private tool does not pile up what was said without reason.
    /// </summary>
    private async Task ReportReturnFailure(int session)
    {
        await Report(new DictationNotice(
            kind: DictationNoticeKind.Warning,
            message: "The text was inserted, but pressing Return failed.")).ConfigureAwait(false);
        await Cleanup(session).ConfigureAwait(false);
    }

    /// <summary>The text was recognized, but could not be inserted - we keep it so that it does not disappear.</summary>
    private async Task HandleInsertionFailure(Exception error, string text, int session)
    {
        if (error is TextInsertionError.InsertedButClipboardRestoreFailed)
        {
            await Report(new DictationNotice(
                kind: DictationNoticeKind.Warning,
                message: "The text was inserted, but the previous clipboard couldn't be restored."))
                .ConfigureAwait(false);
            await Cleanup(session).ConfigureAwait(false);
            return;
        }

        lock (gate) { pendingRecovery = new RecoveredDictation(text); }

        // Not a failure, but a normal situation: the password field is active.
        string message = error is TextInsertionError.SecureInputActive
            ? "Text not inserted: secure input is active. Your text is saved in the menu."
            : "The text couldn't be inserted. It's saved in the menu.";

        await Report(new DictationNotice(
            kind: DictationNoticeKind.Warning,
            message: message,
            recoverableText: text,
            wordsDidNotLand: true)).ConfigureAwait(false);
        await Cleanup(session).ConfigureAwait(false);
    }

    // MARK: - Cancel

    /// <summary>Cancel dictation.</summary>
    public void Cancel()
    {
        int session;
        CancellationTokenSource? running;
        lock (gate)
        {
            if (!DictationStopPolicy.CanCancel(state)) { return; }

            // The order is important: first the flag, then cancel the task.
            // Cancelling a task does not interrupt an already ongoing wait, and
            // the flag is checked after each of them.
            cancellationRequested = true;
            running = finalization;
            session = currentSession;
        }

        running?.Cancel();

        _ = Task.Run(async () =>
        {
            await capture.AbortRecording().ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The recording failed on its own - for example, the disk ran out of space.
    ///
    /// This is not a cancellation: the user did not press anything and keeps
    /// talking. Therefore we stop immediately and explain the reason, instead of
    /// waiting until they finish a phrase that there is nowhere to write down.
    /// </summary>
    public void Interrupt(string reason)
    {
        int session;
        CancellationTokenSource? running;
        lock (gate)
        {
            if (state != DictationState.Preparing && state != DictationState.Listening) { return; }

            cancellationRequested = true;
            running = finalization;
            session = currentSession;
        }

        running?.Cancel();

        var notice = new DictationNotice(kind: DictationNoticeKind.Failure, message: reason);
        _ = Task.Run(async () =>
        {
            // Through Report: the interruption loses the words, and the person
            // mid-sentence learns of it by ear like any other loss.
            await Report(notice).ConfigureAwait(false);
            await capture.AbortRecording().ConfigureAwait(false);
            await FinishWithoutInsertion(session).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// System permission or device disappeared while recording.
    /// Close the WAV and keep it for an explicit Retry/Delete without trying to
    /// recognize or insert into an already untrusted system state.
    /// </summary>
    public void PreserveActiveRecording(string reason)
    {
        lock (gate)
        {
            if (state != DictationState.Preparing && state != DictationState.Listening) { return; }

            if (state == DictationState.Listening)
            {
                if (finalization is not null) { return; }

                int session = currentSession;
                SetState(DictationState.Transcribing);
                finalization = new CancellationTokenSource();
                CancellationToken token = finalization.Token;
                _ = Task.Run(() => Rescue(reason, session, token));
                return;
            }
        }

        Cancel();
        var notice = new DictationNotice(kind: DictationNoticeKind.Failure, message: reason);
        // Through Report, not around it: the notice must carry the attention
        // sound like every other surfaced failure.
        _ = Task.Run(() => Report(notice));
    }

    private async Task Rescue(string reason, int session, CancellationToken token)
    {
        (string Url, TimeSpan Duration)? recording;
        try
        {
            recording = await capture.StopRecording().ConfigureAwait(false);
        }
        catch (Exception)
        {
            recording = null;
        }

        // While the file was closing, the person could press Escape. From that
        // moment the dictation has one outcome - cancellation, and it is more
        // important than the rescue: a closed WAV is no longer deleted by the
        // recording interruption, and a kept one would go into the retry folder
        // contrary to the promise "a cancelled dictation is deleted". At the
        // same time we keep quiet: the failure message would fall on top of the
        // session the person started next.
        if (token.IsCancellationRequested)
        {
            if (recording is { } cancelled) { await Discard(cancelled.Url).ConfigureAwait(false); }

            await Cleanup(session).ConfigureAwait(false);
            return;
        }

        DictationNotice notice;
        if (recording is { } take && !DictationDurationPolicy.IsWorthTranscribing(take.Duration))
        {
            // The device vanished a moment after the start: the take holds not a
            // single word, there is nothing to rescue. The main path deletes such
            // fragments silently - here we name the reason of the interruption
            // and nothing else, otherwise a one-second device hiccup leaves a
            // "recording for retry" whose retry can only ever produce an empty result.
            await Discard(take.Url).ConfigureAwait(false);
            notice = new DictationNotice(kind: DictationNoticeKind.Failure, message: reason);
        }
        else
        {
            string? saved = null;
            if (recording is { } kept)
            {
                try
                {
                    saved = await recordingRecovery.Preserve(kept.Url).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    saved = null;
                }
            }

            notice = new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: saved is null
                    ? reason + " The recording couldn't be kept."
                    : reason + " The recording is kept on this PC for a few days"
                        + " (Settings → Privacy → Open recordings folder).",
                recoveryAudio: saved);
        }

        await Report(notice).ConfigureAwait(false);
        await Cleanup(session).ConfigureAwait(false);
    }

    // MARK: - Completion

    /// <summary>Is the session for which the wait began still ongoing? The gate must be held.</summary>
    private bool IsCurrent(int session) => session == currentSession;

    private bool ShouldContinue(int session, CancellationToken token)
    {
        lock (gate)
        {
            if (!IsCurrent(session)) { return false; }

            return DictationFinalizationPolicy.ShouldContinue(
                state,
                cancellationRequested,
                token.IsCancellationRequested);
        }
    }

    private async Task Fail(int session, DictationError error)
    {
        // There is no reason to show the failure of a cancelled session: the
        // person has already closed it, and the message would fall on top of the
        // one that is going on now.
        lock (gate)
        {
            if (!IsCurrent(session)) { return; }
        }

        await Report(new DictationNotice(
            kind: DictationNoticeKind.Failure,
            message: error.UserMessage)).ConfigureAwait(false);
        await Cleanup(session).ConfigureAwait(false);
    }

    private Task FinishWithoutInsertion(int session) => Cleanup(session);

    /// <summary>
    /// Cleaning after the session, in strict order.
    ///
    /// The microphone is muted here and only here: the promise "the recording
    /// indicator is not lit while we are not listening" relies on this method
    /// being called on every completion path, including errors and cancellation.
    ///
    /// You can only clean up your own session: the tail of a previous one, woken
    /// after a cancellation, would otherwise put out the new one already in progress.
    /// </summary>
    private async Task Cleanup(int session)
    {
        lock (gate)
        {
            if (!IsCurrent(session)) { return; }

            finalization = null;
            deferredStopRequested = false;
            cancellationRequested = false;
            isHandsFree = false;
            SetHandsFreeActive(false);
            targetApplication = null;
            recordingStartedAt = null;
            // The release mark is reset along with the rest of the session state.
            // Otherwise t0 of a cancelled dictation would flow into the next one,
            // and its report would be about a time that includes someone else's wait.
            stopRequestedAt = null;
            SetState(DictationState.Idle);
        }

        await overlay.Dismiss().ConfigureAwait(false);
        // The next session starts able to sound again.
        lock (gate) { hasSoundedThisSession = false; }
    }

    /// <summary>The gate must be held.</summary>
    private TimeSpan ElapsedSeconds() =>
        recordingStartedAt is { } startedAt ? now() - startedAt : TimeSpan.Zero;

    /// <summary>
    /// Show the message: both to the subscriber and on the panel.
    ///
    /// The only way messages exit from the kernel - the attention sound rides on
    /// it, so a path around <see cref="Report"/> is a path around the ear.
    /// </summary>
    private async Task Report(DictationNotice notice)
    {
        bool sound;
        lock (gate)
        {
            OnNotice?.Invoke(notice);
            // One sound per session, and only for words that never landed.
            //
            // "The text was inserted, but Return failed" is shown, not sounded:
            // the words are in the field, the person is looking at them. The ear
            // is reserved for the loss they would otherwise miss.
            sound = notice.WordsDidNotLand && !hasSoundedThisSession;
            if (sound) { hasSoundedThisSession = true; }
        }

        if (sound) { await sounds.PlayAttention().ConfigureAwait(false); }

        await overlay.PresentNotice(notice).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove a recording from disk.
    ///
    /// A separate method so that deletion cannot be accidentally missed on one
    /// of the completion branches: the user's voice should not remain in files
    /// after the text is recognized.
    /// </summary>
    private async Task Discard(string url)
    {
        if (!File.Exists(url)) { return; }

        Exception? failure = null;
        try
        {
            File.Delete(url);
        }
        catch (Exception error)
        {
            failure = error;
        }

        if (failure is not null)
        {
            await Report(new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: $"Couldn't delete the local recording: {failure.Message}")).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Errors that the user sees.
///
/// Insertion failure is not included here intentionally: the text at this moment
/// is already recognized and remains in memory, and the person should be told
/// not "it failed", but how to get it via Copy/Retry. That is what
/// <c>HandleInsertionFailure</c> does.
/// </summary>
/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; nothing
/// ever throws it, so here it is a record hierarchy rather than an exception one -
/// its only job is to word a message.
/// </remarks>
public abstract record DictationError
{
    private protected DictationError()
    {
    }

    public sealed record Capture(string Reason) : DictationError;

    public sealed record Recognition(string Reason) : DictationError;

    public string UserMessage =>
        this switch
        {
            Capture => "Couldn't record audio.",
            Recognition => "Couldn't transcribe speech.",
            _ => throw new InvalidOperationException("unknown dictation error"),
        };
}
