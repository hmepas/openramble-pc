using System.Diagnostics;

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

    /// <summary>Technical cause, before it is replaced by a short user-facing notice.</summary>
    public Action<Exception>? OnRecognitionFailure { get; set; }

    /// <summary>Which stage a failed take was in, how long it had run, and whether it timed out.</summary>
    public Action<DictationFailureStage, TimeSpan, bool>? OnStageFailure { get; set; }

    /// <summary>Why the take had to be recognized whole again, with the error that caused it.</summary>
    public Action<DictationFallbackReason, Exception?>? OnRecognitionFallback { get; set; }

    /// <summary>
    /// A take that produced text, with the audio still on disk, and the app the words were
    /// shaped for.
    ///
    /// Called after insertion is attempted and before the recording is deleted, so an owner
    /// that keeps a history gets both halves of the take while both still exist. Whether the
    /// words actually landed is not the question here - a person who watched an insertion fail
    /// wants that transcript more than anyone, not less.
    ///
    /// Empty text is a take with sound in which the model found no words. It is handed over
    /// too, so it can be played and recognized again rather than lost; only a take of silence
    /// is not.
    ///
    /// Returns whether the take was kept. With no words, the notice tells the person where the
    /// take went, and only the owner knows.
    /// </summary>
    public Func<string, string, TargetApplication?, bool>? OnTakeFinished { get; set; }

    /// <summary>
    /// The name of the microphone the takes are recorded from, for the one message that needs
    /// it: a take that heard only silence.
    /// </summary>
    public Func<string?>? MicrophoneName { get; set; }

    /// <summary>
    /// The take is finished and the model is not resident yet, so the words are waiting for a
    /// load rather than for recognition.
    ///
    /// The panel is the only feedback channel during dictation, and until now it said
    /// "Transcribing…" through the whole wait - up to the preparation deadline. Calling a wait
    /// work is a small lie that becomes a large one on a short take, which has no speech for the
    /// load to hide under: the person watches a spinner claim to transcribe half a second of
    /// audio for fifteen seconds. <c>true</c> opens the wait, <c>false</c> closes it on every
    /// exit, including a throw.
    /// </summary>
    public Action<bool>? OnEnginePreparationWait { get; set; }

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

    /// <summary>
    /// Reopen file-backed PCM after the capture released its bounded memory copy.
    /// </summary>
    private readonly Func<string, CancellationToken, Task<float[]>>? readSamples;

    /// <summary>
    /// Sample rate every part of this app agrees on. Named rather than spelled out at the
    /// places below that need it.
    /// </summary>
    private const int SampleRate = 16_000;

    /// <summary>
    /// Below this a piece of audio is not handed to the engine on its own.
    ///
    /// Measured against the shipping model: under about two seconds its output is not merely
    /// worse but non-monotonic - the same start offset returned text at 0.8 s, nothing at 1.0 s,
    /// text again at 1.2 s. A tail shorter than this is recognized together with the segment
    /// before it instead.
    /// </summary>
    private const int MinimumTailSamples = 2 * SampleRate;

    private readonly ITextInserting inserter;
    private readonly IOverlayPresenting overlay;
    private readonly ISounding sounds;
    private readonly IRecordingRecoveryStoring recordingRecovery;
    /// <summary>
    /// Built per take, for the app the words are going to: a terminal gets verbatim text and may
    /// honour "Send it.", a mail window gets paragraphs.
    /// </summary>
    private readonly Func<TargetApplication?, ITextProcessing> pipeline;

    /// <summary>
    /// Key-down destination. Production may supply a pre-populated snapshot so that no system
    /// query sits between the key going down and the microphone being asked for; the default
    /// asks the inserter, exactly as before.
    /// </summary>
    private readonly Func<TargetApplication?> targetApplicationSnapshot;

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

    /// <summary>
    /// How long a stop-time model wait may stay silent before the panel says so. Injectable so
    /// suites can prove both the announcement and the silence without sleeping a
    /// production-scale interval.
    /// </summary>
    private readonly TimeSpan enginePreparationNoticeDelay;

    /// <summary>
    /// Clipboard, window activation and synthesized input are system edges. A
    /// cancellation-deaf implementation must not leave the controller in
    /// <see cref="DictationState.Inserting"/> forever; an uncertain result is retained in memory.
    /// </summary>
    private readonly TimeSpan insertionDeadline;

    /// <summary>
    /// Return is a separate irreversible system event after text already landed. It has its own
    /// shorter confirmation budget.
    /// </summary>
    private readonly TimeSpan returnDeadline;

    /// <summary>
    /// How long a cancellation waits for the microphone to close before freeing the session.
    /// </summary>
    /// <remarks>
    /// A device edge that wedged is the reason Escape exists; Escape waiting on that same edge
    /// would defeat itself. The abort keeps running in the background and closes the microphone
    /// whenever the system lets it - this only bounds how long the panel and the next dictation
    /// wait for it, which for a healthy device is never.
    /// </remarks>
    private static readonly TimeSpan AbortForegroundGrace = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long closing the take may run before the capture is presumed wedged.
    /// </summary>
    /// <remarks>
    /// <b>Deviation from the macOS original, deliberate.</b> There, and here until now, the
    /// stretch between the key release and the first watchdog was covered by nothing at all:
    /// <c>StopRecording</c> was a bare await. Every other stretch of the finalization has a
    /// deadline, so a capture that never closes its file was the one remaining way to hold
    /// "Transcribing…" forever with no notice, no sound and no way out but Escape - which is
    /// exactly the hang the deadline apparatus exists to prevent. Closing a WAV whose queue is
    /// already drained is milliseconds of work, so no healthy stop ever meets this.
    /// </remarks>
    private readonly TimeSpan captureStopDeadline;

    /// <summary>
    /// The budget every stop is given when the owner does not name one.
    /// </summary>
    /// <remarks>
    /// Public so the platform's capture can size its own waits against it. A capture whose
    /// internal budgets added up to more than this could only ever report the wedge this
    /// deadline exists to catch, with the recording already safely closed.
    /// </remarks>
    public static TimeSpan DefaultCaptureStopDeadline { get; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the microphone may take to start, from the key-down, before the person is told.
    /// </summary>
    /// <remarks>
    /// Five seconds rather than three: the slowest successful start upstream measured in a week
    /// took under three, and Bluetooth profile switches are unmeasured. An early notice ends a
    /// real take; a late one only delays news about a broken one.
    /// </remarks>
    private readonly TimeSpan microphoneStartDeadline;

    /// <summary>
    /// How long a tap released before the microphone opened waits for it. A tap is a change of
    /// mind, and this bound is what keeps a microphone that never opens from holding it.
    /// </summary>
    private readonly TimeSpan preparingStopDeadline;

    /// <summary>
    /// How long a take that came back without words may spend reading its file back to tell
    /// silence from sound.
    /// </summary>
    private readonly TimeSpan recordingReadableDeadline;

    // MARK: - Session state

    /// <summary>
    /// The take in progress, from its key-down to its cleanup; <c>null</c> between takes.
    /// </summary>
    /// <remarks>
    /// Everything that belongs to one dictation lives on it, and every continuation carries its
    /// own take rather than reading this field again after an await: by then the take may be
    /// over and the field may hold the next one. See <see cref="Take"/>.
    /// </remarks>
    private Take? take;

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

    /// <summary>
    /// That same number, readable from outside.
    /// </summary>
    /// <remarks>
    /// For the diagnostic log and nothing else: a dictation crosses four components, each of
    /// which writes its own events, and without a shared number the recognition and the insertion
    /// of one session cannot be told from the next one's in a file of them. No decision is taken
    /// on it here - the session identity that matters inside is the private field.
    /// </remarks>
    public int CurrentSession { get { lock (gate) { return currentSession; } } }

    public DictationController(
        IAudioCapturing capture,
        Func<string, CancellationToken, Task<ASRResult>> transcribe,
        ITextInserting inserter,
        IOverlayPresenting overlay,
        ISounding sounds,
        Func<float[], CancellationToken, Task<ASRResult>>? transcribeSamples = null,
        IRecordingRecoveryStoring? recordingRecovery = null,
        Func<TargetApplication?, ITextProcessing>? pipeline = null,
        Func<DateTimeOffset>? now = null,
        Func<MonotonicInstant>? monotonicNow = null,
        Func<TimeSpan, TimeSpan>? transcriptionDeadline = null,
        Func<CancellationToken, Task>? prepareForTranscription = null,
        // Worst measured cold reload is a handful of seconds of graph specialization plus the
        // warm-up; 25 s covers it with margin while a genuine wedge still surfaces well before
        // anyone gives up on the panel.
        TimeSpan? prepareDeadline = null,
        TimeSpan? captureStopDeadline = null,
        Func<TargetApplication?>? targetApplicationSnapshot = null,
        TimeSpan? enginePreparationNoticeDelay = null,
        TimeSpan? insertionDeadline = null,
        TimeSpan? returnDeadline = null,
        Func<string, CancellationToken, Task<float[]>>? readSamples = null,
        TimeSpan? microphoneStartDeadline = null,
        TimeSpan? preparingStopDeadline = null,
        TimeSpan? recordingReadableDeadline = null)
    {
        this.capture = capture;
        this.transcribe = transcribe;
        this.transcribeSamples = transcribeSamples;
        this.readSamples = readSamples;
        this.microphoneStartDeadline = microphoneStartDeadline ?? TimeSpan.FromSeconds(5);
        this.preparingStopDeadline = preparingStopDeadline ?? TimeSpan.FromMilliseconds(500);
        this.recordingReadableDeadline = recordingReadableDeadline ?? TimeSpan.FromSeconds(10);
        this.inserter = inserter;
        this.overlay = overlay;
        this.sounds = sounds;
        this.recordingRecovery = recordingRecovery ?? new DiscardingRecordingRecovery();
        this.pipeline = pipeline ?? (static _ => new TextPipeline());
        this.now = now ?? (static () => DateTimeOffset.Now);
        this.monotonicNow = monotonicNow ?? (static () => MonotonicInstant.Now);
        this.transcriptionDeadline = transcriptionDeadline ?? TranscriptionDeadline.Deadline;
        this.prepareForTranscription = prepareForTranscription;
        this.prepareDeadline = prepareDeadline ?? TimeSpan.FromSeconds(25);
        this.captureStopDeadline = captureStopDeadline ?? DefaultCaptureStopDeadline;
        this.targetApplicationSnapshot = targetApplicationSnapshot ?? inserter.FrontmostApplication;
        this.enginePreparationNoticeDelay = enginePreparationNoticeDelay ?? TimeSpan.FromMilliseconds(250);
        this.insertionDeadline = insertionDeadline ?? TimeSpan.FromSeconds(2);
        this.returnDeadline = returnDeadline ?? TimeSpan.FromSeconds(1);
    }

    // MARK: - Beginning

    /// <summary>Hotkey pressed.</summary>
    public void Begin(bool handsFree, bool isEnabled, bool isModelReady)
    {
        Take started;
        lock (gate)
        {
            // The first check is synchronous, before any waiting. Between it and
            // the asynchronous start, a second press can arrive.
            if (!DictationStopPolicy.CanStart(state, isEnabled, isModelReady)) { return; }

            currentSession += 1;
            // Do not touch the previous Copy/Retry: the new recording can be
            // cancelled or fail with an error. Saved text is deleted only by an
            // explicit action or after a successful Retry.
            //
            // One stream per take. Built here rather than lazily so the sink is in place before
            // the first frame: a segment cut before anybody was listening would be audio nobody
            // ever recognizes.
            started = new Take(
                currentSession,
                MonotonicInstant.Now,
                transcribeSamples is { } recognizeSamples ? new StreamedSegmentRecognizer(recognizeSamples) : null)
            {
                IsHandsFree = handsFree,
            };
            take = started;
            SetHandsFreeActive(handsFree);
            // One watchdog per take, armed at the key-down: a microphone that never opens is
            // reported while the key is still held, not when it comes up.
            ArmMicrophoneStartWatchdog(started, started.BeganAt.Advanced(microphoneStartDeadline));
            SetState(DictationState.Preparing);
        }

        // We remember the target right away: the focus will go away later. Asked off this
        // thread and outside the gate, alongside the microphone rather than in front of it: the
        // query is a round trip to the window system, and every millisecond of it here would be
        // a millisecond of the first word lost and of the key-up waiting for the gate.
        started.ResolveTarget(targetApplicationSnapshot);

        _ = Task.Run(async () =>
        {
            // A capture device can take a noticeable fraction of a second to wake up.
            // Acknowledge the hotkey before that wait so the gesture never feels lost.
            await overlay.Present(DictationState.Preparing, TimeSpan.Zero).ConfigureAwait(false);
            await StartCapture(started).ConfigureAwait(false);
        });
    }

    private async Task StartCapture(Take take)
    {
        // Second check of the same condition: between the synchronous part and
        // this line a transition between tasks has happened, during which the
        // session could be cancelled. Whoever ended it owns the cleanup.
        if (!IsPreparingCurrentSession(take)) { return; }

        try
        {
            // The sink hops nothing and holds nothing: `Submit` is safe from the capture's own
            // queue and returns at once. It travels with the start, so no cut can be made while
            // nobody is listening and none can reach another take's stream.
            StreamedSegmentRecognizer? streamed = take.Streamed;
            await capture.StartRecording(take.Session, streamed is null ? null : streamed.Submit).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A start that failed on its own ends the take as an interruption of the recording.
            // One that failed because the take was already ended is that ending's business.
            bool claimed;
            lock (gate) { claimed = take.TryClaimEnding(EndingOwner.Interrupt); }

            if (claimed) { await Fail(take, EndingOwner.Interrupt, new DictationError.Capture(error.ToString())).ConfigureAwait(false); }
            return;
        }

        // After waiting, the phase is checked again - the cancellation could
        // have come exactly at the moment the engine starts. Checked in the same turn of the
        // gate as the transition, so a watchdog firing in between cannot be overwritten.
        bool listening;
        lock (gate)
        {
            listening = take.TryStartListening();
            if (listening)
            {
                take.RecordingStartedAt = now();
                SetState(DictationState.Listening);
            }
        }

        if (!listening)
        {
            // The recording has started, but the take was ended while it was starting. We turn
            // off our microphone - otherwise it stays on and there is nothing left to stop it -
            // and nothing else: the path that ended the take owns its notice and its cleanup.
            await capture.AbortRecording(take.Session).ConfigureAwait(false);
            return;
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
            deferred = IsCurrent(take) && take.DeferredStopRequested;
            if (deferred) { take.DeferredStopRequested = false; }
        }

        if (deferred) { Finish(take); }
    }

    private bool IsPreparingCurrentSession(Take take)
    {
        lock (gate)
        {
            return take.Phase == TakePhase.Preparing;
        }
    }

    // MARK: - Stop

    /// <summary>The hotkey is released (or pressed a second time in hands-free mode).</summary>
    public void Stop()
    {
        DeferredStopDecision decision;
        Take? stopping;
        lock (gate)
        {
            stopping = take;
            decision = DictationStopPolicy.DecideStop(state, stopping?.IsHandsFree ?? false);
            if (stopping is null) { return; }

            switch (decision)
            {
                case DeferredStopDecision.StopNow:
                    MarkStopRequested(stopping);
                    break;
                case DeferredStopDecision.DeferUntilListening:
                    // The release is the person's "stop", whenever the microphone gets round to
                    // hearing it: a cold or blocked capture start after key release must not be
                    // hidden from the speed report.
                    MarkStopRequested(stopping);
                    stopping.DeferredStopRequested = true;
                    break;
            }
        }

        if (decision == DeferredStopDecision.StopNow) { Finish(stopping); }
        if (decision == DeferredStopDecision.DeferUntilListening) { StopArrivedBeforeTheMicrophoneOpened(stopping); }
    }

    /// <summary>
    /// Capture reached its bounded in-memory limit. Finish the complete retained take through
    /// the ordinary transcription path, independent of hotkey/hands-free gesture policy. This is
    /// a graceful capacity stop, not a capture failure.
    /// </summary>
    /// <param name="session">
    /// The take that reached its limit. Any other session's is late news about a take that is
    /// already over, and stops nothing.
    /// </param>
    /// <returns>Whether a running take was stopped by it, so its owner can say why.</returns>
    public bool StopAtCaptureMemoryLimit(int session)
    {
        bool stopNow = false;
        bool deferred = false;
        Take? limited;
        lock (gate)
        {
            limited = take;
            if (limited?.Session != session) { return false; }

            switch (limited.Phase)
            {
                case TakePhase.Listening:
                    MarkStopRequested(limited);
                    stopNow = true;
                    break;
                case TakePhase.Preparing:
                    MarkStopRequested(limited);
                    limited.DeferredStopRequested = true;
                    deferred = true;
                    break;
                case TakePhase.Ending:
                case TakePhase.Done:
                    return false;
            }
        }

        if (stopNow) { Finish(limited); }
        if (deferred) { StopArrivedBeforeTheMicrophoneOpened(limited); }
        return true;
    }

    /// <summary>Stop in the mode without holding - by pressing the key a second time.</summary>
    public void StopHandsFree()
    {
        bool stopNow = false;
        bool deferred = false;
        Take? stopping;
        lock (gate)
        {
            stopping = take;
            if (stopping is null || !stopping.IsHandsFree) { return; }

            switch (stopping.Phase)
            {
                case TakePhase.Listening:
                    MarkStopRequested(stopping);
                    stopNow = true;
                    break;
                case TakePhase.Preparing:
                    // Same as releasing the key, only pressing: the second press
                    // arrived before the engine rose. Losing it is not allowed -
                    // in this mode the key is not held down and nothing else
                    // stops the recording: the microphone would stay on with no
                    // one listening.
                    MarkStopRequested(stopping);
                    stopping.DeferredStopRequested = true;
                    deferred = true;
                    break;
                case TakePhase.Ending:
                case TakePhase.Done:
                    break;
            }
        }

        if (stopNow) { Finish(stopping); }
        if (deferred) { StopArrivedBeforeTheMicrophoneOpened(stopping); }
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
            if (take is not { } promoted
                || (promoted.Phase != TakePhase.Preparing && promoted.Phase != TakePhase.Listening)) { return; }

            // Commit every internal mode transition before publishing the external hands-free
            // callback. That callback is reentrant and may immediately stop the session;
            // clearing its newly accepted stop afterwards would leave the microphone recording
            // forever.
            promoted.IsHandsFree = true;
            promoted.DeferredStopRequested = false;
            // A tap's release may have pulled the watchdog in. That bound belonged to the tap; a
            // hands-free take is ended only by a microphone that misses the deadline from its
            // key-down.
            promoted.CancelMicrophoneStartWatchdog();
            if (promoted.Phase == TakePhase.Preparing)
            {
                ArmMicrophoneStartWatchdog(promoted, promoted.BeganAt.Advanced(microphoneStartDeadline));
            }

            // The release that promoted the session belonged to the previous gesture, and so
            // did its "stop" mark: in the new mode the stop is still to come.
            promoted.StopRequestedAt = null;
            SetHandsFreeActive(true);
        }
    }

    private void Finish(Take finishing)
    {
        CancellationToken token;
        lock (gate)
        {
            // Finalization runs exactly once: otherwise the text will be inserted twice. Only a
            // listening take can be finished; one whose ending is already claimed - by Escape
            // among others - is not this stop's to finish.
            if (!finishing.TryClaimEnding(EndingOwner.Finish)) { return; }

            // One entry for all the stop paths: Stop, StopHandsFree and the
            // deferred release - all of them come here.
            MarkStopRequested(finishing);
            SetState(DictationState.Transcribing);
            finishing.Finalization = new CancellationTokenSource();
            token = finishing.Finalization.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Finalize(finishing, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Every expected failure is handled inside, each with its own notice. This is the
                // last line under them: whatever escaped, the take must not hold "Transcribing…"
                // forever with the microphone free and nobody told.
                bool owned;
                lock (gate) { owned = finishing.Owns(EndingOwner.Finish); }

                if (!owned) { return; }

                await Report(finishing, EndingOwner.Finish, new DictationNotice(
                    kind: DictationNoticeKind.Failure,
                    message: new DictationError.Recognition("unexpected failure").UserMessage)).ConfigureAwait(false);
                await Cleanup(finishing, EndingOwner.Finish).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// A release (or the second press of a hands-free take) arrived before the microphone opened.
    /// </summary>
    /// <remarks>
    /// The take can no longer record anything: it ends at the moment the microphone finally
    /// opens, so its recording would be empty whatever happens next. Which empty take it is, the
    /// hold decides, by the same rule as any other empty take
    /// (<see cref="DictationDurationPolicy.OutcomeForShortRecording"/>). After a real hold the
    /// person spoke into a microphone that was not there yet, and waiting for it only to drop an
    /// empty take without a word is how a sentence vanished. They hear it now. A shorter press is
    /// a change of mind; it stays quiet and waits at most <see cref="preparingStopDeadline"/> for
    /// the microphone.
    /// </remarks>
    private void StopArrivedBeforeTheMicrophoneOpened(Take stopping)
    {
        bool reportNow;
        lock (gate)
        {
            if (stopping.Phase != TakePhase.Preparing) { return; }

            TimeSpan held = stopping.BeganAt.DurationTo(MonotonicInstant.Now);
            reportNow = DictationDurationPolicy.OutcomeForShortRecording(held)
                == DictationDurationPolicy.ShortRecordingOutcome.ReportSilentInput;
            if (!reportNow)
            {
                ArmMicrophoneStartWatchdog(stopping, MonotonicInstant.Now.Advanced(preparingStopDeadline));
            }
        }

        if (reportNow) { MicrophoneStartTookTooLong(stopping); }
    }

    /// <summary>The gate must be held. Keeps the earlier of the current deadline and the new one.</summary>
    /// <remarks>
    /// On the real clock, not the injected one: the injected clock exists to script the speed
    /// report's marks, and a watchdog reading it would consume them.
    /// </remarks>
    private void ArmMicrophoneStartWatchdog(Take watched, MonotonicInstant deadline)
    {
        if (watched.MicrophoneStartWatchdog is not null
            && watched.MicrophoneStartWatchdogDeadline is { } armed
            && armed.Timestamp <= deadline.Timestamp)
        {
            return;
        }

        watched.MicrophoneStartWatchdog?.Cancel();
        var watchdog = new CancellationTokenSource();
        watched.MicrophoneStartWatchdog = watchdog;
        watched.MicrophoneStartWatchdogDeadline = deadline;
        TimeSpan remaining = MonotonicInstant.Now.DurationTo(deadline);
        // A timer, not a task started now: nothing runs until the deadline, so arming this at the
        // key-down takes no thread away from the capture start it is watching.
        _ = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, watchdog.Token).ContinueWith(
            _ => MicrophoneStartTookTooLong(watched),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>
    /// The microphone did not open in time. The take ends and says so; the notice carries the
    /// attention sound.
    /// </summary>
    /// <remarks>
    /// Nothing is aborted here: the start is still running, and when it finally returns it
    /// finds its session no longer preparing and closes the microphone itself - the same path a
    /// cancellation during the start already takes.
    /// </remarks>
    private void MicrophoneStartTookTooLong(Take late)
    {
        lock (gate)
        {
            // Claims the ending, so neither a stop nor the late start can finish it.
            if (!late.TryClaimEnding(EndingOwner.Watchdog)) { return; }

            SetState(DictationState.Transcribing);
        }

        late.Streamed?.Cancel();
        _ = Task.Run(async () =>
        {
            await Report(late, EndingOwner.Watchdog, new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: "Starting the microphone took too long. Dictation was stopped.",
                wordsDidNotLand: true)).ConfigureAwait(false);
            await Cleanup(late, EndingOwner.Watchdog).ConfigureAwait(false);
        });
    }

    /// <summary>The gate must be held. The first stop request of a session is its t0; later ones keep it.</summary>
    private void MarkStopRequested(Take stopping)
    {
        stopping.StopRequestedAt ??= monotonicNow();
    }

    /// <summary>
    /// Whether the panel is currently telling someone that the model is still loading. Kept so
    /// the retraction is exactly as conditional as the announcement: a <c>false</c> nobody was
    /// told about would be a lie of its own.
    /// </summary>
    private bool isAnnouncingEngineWait;

    private void BeginEngineWaitAnnouncement()
    {
        Action<bool>? announce;
        lock (gate)
        {
            if (isAnnouncingEngineWait) { return; }

            isAnnouncingEngineWait = true;
            announce = OnEnginePreparationWait;
        }

        announce?.Invoke(true);
    }

    private void EndEngineWaitAnnouncement()
    {
        Action<bool>? announce;
        lock (gate)
        {
            if (!isAnnouncingEngineWait) { return; }

            isAnnouncingEngineWait = false;
            announce = OnEnginePreparationWait;
        }

        announce?.Invoke(false);
    }

    /// <summary>Move the take into safekeeping and word the failure notice around the outcome.</summary>
    /// <param name="lead">What went wrong; the notice adds where the recording is.</param>
    private async Task<DictationNotice> PreserveForRetry(string url, string lead)
    {
        try
        {
            string? saved = await recordingRecovery.Preserve(url).ConfigureAwait(false);
            return new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: lead + (saved is null
                    ? " The recording couldn't be kept."
                    : " The recording is kept on this PC for a few days"
                        + " (Settings → Privacy → Open recordings folder)."),
                recoveryAudio: saved);
        }
        catch (Exception error)
        {
            const string kept = " The recording is still on disk, but safekeeping failed";
            return new DictationNotice(
                kind: DictationNoticeKind.Failure,
                message: $"{lead}{kept}: {error.Message}")
            {
                LogMessage = $"{lead}{kept}: {error.GetType().Name}",
            };
        }
    }

    private async Task Finalize(Take take, CancellationToken token)
    {
        TimeSpan elapsed;
        lock (gate) { elapsed = ElapsedSeconds(take); }

        await overlay.Present(DictationState.Transcribing, elapsed).ConfigureAwait(false);

        (string Url, TimeSpan Duration) recording;
        // Stage timings for the journal read the real clock, not the injected one: tests script
        // that clock read by read for the speed report, and these reads must not shift it.
        MonotonicInstant freezeStarted = MonotonicInstant.Now;
        try
        {
            // The caller's token is deliberately not passed in: Escape during the stop keeps the
            // meaning it always had - the file is closed, and the checks below discard it quietly.
            // Only the deadline is new, and it exists because this was the one stretch after the
            // release that nothing bounded.
            recording = await TranscriptionDeadline.WithTranscriptionDeadline(
                captureStopDeadline,
                _ => capture.StopRecording(take.Session)).ConfigureAwait(false);
        }
        catch (TranscriptionTimeout)
        {
            if (ShouldContinue(take, token))
            {
                OnStageFailure?.Invoke(DictationFailureStage.CaptureFreeze, freezeStarted.DurationTo(MonotonicInstant.Now), true);
            }

            await AbandonWedgedCapture(take, token).ConfigureAwait(false);
            return;
        }
        catch (Exception error)
        {
            if (ShouldContinue(take, token))
            {
                OnStageFailure?.Invoke(DictationFailureStage.CaptureFreeze, freezeStarted.DurationTo(MonotonicInstant.Now), false);
            }

            await Fail(take, EndingOwner.Finish, new DictationError.Capture(error.ToString())).ConfigureAwait(false);
            return;
        }

        // Move the in-memory buffer out immediately so every early-return path
        // releases it. The on-disk take remains available for retry/recovery.
        float[]? bufferedSamples = await capture.TakeBufferedSamples(take.Session).ConfigureAwait(false);
        // How much of that buffer already left in segments. Read after the stop, like the
        // buffer: the stop is the barrier past which the count is exact.
        int consumedSamples = await capture.ConsumedSampleCount(take.Session).ConfigureAwait(false);
        // Frozen with this exact take, here rather than after recognition: a read at that point
        // was one more wait between the engine's answer and the pipeline, and one more place for
        // an old session to resume inside the next one.
        TimeSpan? microphoneStartup = await capture.StartupLatency(take.Session).ConfigureAwait(false);
        // The capture is behind us. Marking it here, before any branch below, keeps the stage
        // boundary at the causal event rather than at whatever check happens to run next.
        MonotonicInstant freezeCompletedAt = monotonicNow();

        if (!ShouldContinue(take, token))
        {
            await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
            await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
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
            lock (gate) { held = ElapsedSeconds(take); }

            DictationDurationPolicy.ShortRecordingOutcome outcome =
                DictationDurationPolicy.OutcomeForShortRecording(held);
            await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
            switch (outcome)
            {
                case DictationDurationPolicy.ShortRecordingOutcome.DropSilently:
                    await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
                    break;
                case DictationDurationPolicy.ShortRecordingOutcome.ReportSilentInput:
                    await Report(take, EndingOwner.Finish, new DictationNotice(
                        kind: DictationNoticeKind.Failure,
                        message: "The microphone recorded nothing — check that the right input device is selected and not muted."))
                        .ConfigureAwait(false);
                    await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
                    break;
            }

            return;
        }

        // Judged before recognition, because recognition may level the buffer in place, and a
        // levelled hiss is no longer silence. Stops at the first loud sample, so on an ordinary
        // take this costs nothing.
        bool? heardOnlySilence = bufferedSamples is null ? null : SilentTakePolicy.HeardOnlySilence(bufferedSamples);

        ASRResult recognized;
        // Stage boundaries for the speed report. Plain clock reads on the path that already
        // runs; none of them adds a wait, a lock or a reordered call.
        MonotonicInstant? preparationCompletedAt = null;
        // The instant the recognition call is dispatched, and how long the whole call took as
        // seen from here. Everything between the dispatch and the engine's own clock is
        // transport: hopping threads, entering the transcriber, waiting for a turn. That span
        // held the whole of every slow take and had no number.
        TimeSpan? dispatch = null;
        // How long the file path waited for the recording to be readable: from entering that
        // path to having the take's audio in hand. Absent on the in-memory path - which is what
        // tells the two apart in the journal and the speed line. Written by the work below, read
        // only after it has returned.
        TimeSpan? recordingReadable = null;
        // For the local journal: which stage a failure happened in, and why a streamed take had
        // to be recognized whole again. Numbers and case names only, never the error text.
        DictationFailureStage? stage = null;
        MonotonicInstant stageStarted = default;
        StreamedSegmentRecognizer? streamed = take.Streamed;
        void RecordStageFailure(bool timedOut)
        {
            if (stage is { } failed)
            {
                OnStageFailure?.Invoke(failed, stageStarted.DurationTo(MonotonicInstant.Now), timedOut);
            }
        }

        // Recovery does not erase it: a take that came back whole is still worth a line. Sealed
        // as it is read, so recognition work abandoned at its deadline cannot add one later.
        void ReportFallback()
        {
            if (take.SealFallback() is { } error && ShouldContinue(take, token))
            {
                OnRecognitionFallback?.Invoke(DictationFallbackReason.SegmentFailed, error);
            }
        }

        try
        {
            // A residency-managed engine may be cold here; the reload has been
            // running under the person's voice since the keypress and gets its
            // own budget. Only after the engine is in memory does the
            // recognition deadline start counting. Waiting it out and inserting
            // the words beats abandoning them.
            if (prepareForTranscription is { } prepare)
            {
                stage = DictationFailureStage.ModelPreparation;
                stageStarted = MonotonicInstant.Now;
                // A resident engine answers this in microseconds, and flipping the panel to a
                // loading message for every take would be a flicker on the overwhelmingly common
                // path. The wait is announced only once it is long enough for someone to be
                // looking at it, and retracted on every exit.
                using var announcement = new CancellationTokenSource();
                CancellationToken announcementToken = announcement.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(enginePreparationNoticeDelay, announcementToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!announcementToken.IsCancellationRequested) { BeginEngineWaitAnnouncement(); }
                });

                try
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
                catch (TranscriptionTimeout)
                {
                    throw new DictationPreparationTimeout();
                }
                finally
                {
                    announcement.Cancel();
                    EndEngineWaitAnnouncement();
                }

                preparationCompletedAt = monotonicNow();
            }

            // Stamped on this side of the deadline wrapper, so the transport into the engine is
            // inside the number rather than past every stamp.
            MonotonicInstant dispatchedAt = monotonicNow();
            stage = DictationFailureStage.Recognition;
            stageStarted = MonotonicInstant.Now;

            // The deadline stands between the person and a wedged engine: an
            // inference call stuck on a dead system service ignores cancellation
            // and would hold "Transcribing…" forever.
            recognized = await TranscriptionDeadline.WithTranscriptionDeadline(
                transcriptionDeadline(recording.Duration),
                async workToken =>
                {
                    if (transcribeSamples is { } samples && bufferedSamples is { Length: > 0 })
                    {
                        // Whatever was recognized while the person was still talking. `Finish`
                        // waits for the segment in flight rather than cancelling it: cancelling
                        // would save a few tens of milliseconds and cost a full re-decode of
                        // audio whose text is still needed.
                        StreamedSegmentRecognizer.Outcome? outcome = streamed is null
                            ? null
                            : await streamed.Finish().ConfigureAwait(false);
                        if (outcome is StreamedSegmentRecognizer.Outcome.Recognized { Texts.Count: > 0 } pieces)
                        {
                            return await JoinStreamed(
                                pieces.Texts,
                                streamed!.RecognizedWords,
                                streamed!.SubmittedSampleCounts,
                                consumedSamples,
                                bufferedSamples,
                                samples,
                                workToken).ConfigureAwait(false);
                        }

                        // No cuts were made, or a segment failed. Either way the whole take is
                        // recognized here exactly as it always was - a stream that could not
                        // finish costs latency, never words.
                        if (outcome is StreamedSegmentRecognizer.Outcome.Failed failedStream)
                        {
                            take.RecordFallback(failedStream.Error);
                        }

                        return await samples(bufferedSamples, workToken).ConfigureAwait(false);
                    }

                    // The memory cap changes where audio lives, not which words have already
                    // been recognized. Reopen the complete local WAV and reuse the same
                    // prefix/tail join, so a long take is not recognized again from the start.
                    StreamedSegmentRecognizer.Outcome? fileOutcome = streamed is null
                        ? null
                        : await streamed.Finish().ConfigureAwait(false);
                    if (fileOutcome is StreamedSegmentRecognizer.Outcome.Failed failedFileStream)
                    {
                        take.RecordFallback(failedFileStream.Error);
                    }

                    long readableStarted = Stopwatch.GetTimestamp();
                    if (transcribeSamples is { } tailRecognizer
                        && readSamples is { } read
                        && fileOutcome is StreamedSegmentRecognizer.Outcome.Recognized { Texts.Count: > 0 } filePieces)
                    {
                        float[] fileSamples = await read(recording.Url, workToken).ConfigureAwait(false);
                        TimeSpan decoding = Stopwatch.GetElapsedTime(readableStarted);
                        recordingReadable = decoding;
                        workToken.ThrowIfCancellationRequested();
                        if (Math.Abs(fileSamples.Length - recording.Duration.TotalSeconds * SampleRate) >= 1)
                        {
                            throw new ASREngineError.UnsupportedAudioFormat("incomplete file-backed recording");
                        }

                        return await JoinStreamed(
                            filePieces.Texts,
                            streamed!.RecognizedWords,
                            streamed!.SubmittedSampleCounts,
                            consumedSamples,
                            fileSamples,
                            tailRecognizer,
                            workToken,
                            decoding).ConfigureAwait(false);
                    }

                    // The engine reads the file itself. The stop closed it before returning, so
                    // it was readable the moment this path was taken; the number is what it took
                    // to get here, and its presence is what says the take went through the file.
                    recordingReadable = Stopwatch.GetElapsedTime(readableStarted);
                    return await transcribe(recording.Url, workToken).ConfigureAwait(false);
                },
                token).ConfigureAwait(false);
            dispatch = dispatchedAt.DurationTo(monotonicNow());
            ReportFallback();
        }
        catch (DictationPreparationTimeout)
        {
            if (!ShouldContinue(take, token))
            {
                await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
                await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
                return;
            }

            RecordStageFailure(true);

            // A cold engine may still be loading normally. Free the foreground session and keep
            // the take, but do not signal the inference-stall hook: the owner's own preparation
            // retry owns recovery if that load is genuinely wedged, and recycling a load that is
            // about to become ready would only make the next take cold again.
            DictationNotice failed = await PreserveForRetry(
                recording.Url,
                "The speech model is still getting ready; this take was not discarded.").ConfigureAwait(false);
            await Report(take, EndingOwner.Finish, failed).ConfigureAwait(false);
            await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }
        catch (TranscriptionTimeout)
        {
            ReportFallback();
            if (!ShouldContinue(take, token))
            {
                await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
                await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
                return;
            }

            RecordStageFailure(true);
            // Signal containment at the moment the deadline is known. Recovery file I/O and
            // user feedback must never postpone fencing the engine.
            lock (gate) { OnTranscriptionStall?.Invoke(); }

            DictationNotice failed = await PreserveForRetry(
                recording.Url,
                "Transcribing took too long and was stopped.").ConfigureAwait(false);
            await Report(take, EndingOwner.Finish, failed).ConfigureAwait(false);
            await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }
        catch (Exception error)
        {
            // A cancellation that came while the engine was running is more
            // important than its failure. Otherwise Escape would leave exactly
            // the trace it gets rid of: the voice and the failure message saved
            // for Retry, on top of the session the person started next.
            //
            // A cancellation the person did not ask for - a dependency throwing
            // its own cancellation while the session is still live - is not
            // Escape. It is a recognition that did not happen, and it is
            // reported like one rather than swallowed as a quiet exit.
            ReportFallback();
            if (!ShouldContinue(take, token))
            {
                await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
                await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
                return;
            }

            OnRecognitionFailure?.Invoke(error);
            RecordStageFailure(false);
            DictationNotice failed = await PreserveForRetry(
                recording.Url,
                new DictationError.Recognition(error.ToString()).UserMessage).ConfigureAwait(false);
            await Report(take, EndingOwner.Finish, failed).ConfigureAwait(false);
            await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        // t1: the engine returned the text. Taken before all the checks below, so
        // that the number does not include our own branching.
        MonotonicInstant recognizedAt = monotonicNow();

        // Asked at the key-down and answered long ago on every ordinary take; this is the last
        // await before the words are shaped for it.
        TargetApplication? insertionTarget = await take.Target.ConfigureAwait(false);

        // Check after each wait: while recognition was in progress, the user
        // could press cancel. After the last one, so nothing below - history, the notice, the
        // pipeline - can run for a take the person already ended.
        if (!ShouldContinue(take, token))
        {
            await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
            await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        // Recognition owns everything after preparation; without a preparation hook it owns
        // everything after the freeze. `StopRequestedAt` is set on every path that reaches
        // here, so the freeze stage is never guessed.
        MonotonicInstant recognitionStartedAt = preparationCompletedAt ?? freezeCompletedAt;
        // Zero unless the take was actually cut, which is what the log needs to distinguish
        // "fast because it was short" from "fast because most of it was already done".
        int streamedSegmentCount = streamed?.RecognizedCount ?? 0;
        MonotonicInstant? stopMark;
        lock (gate) { stopMark = take.StopRequestedAt; }

        DictationPhaseBreakdown? phases = stopMark is { } stop
            ? new DictationPhaseBreakdown(
                CaptureFreeze: stop.DurationTo(freezeCompletedAt),
                EnginePreparation: preparationCompletedAt is { } prepared
                    ? freezeCompletedAt.DurationTo(prepared)
                    : null,
                Recognition: recognitionStartedAt.DurationTo(recognizedAt),
                EngineProcessing: Positive(recognized.ProcessingDuration),
                AudioDuration: recognized.AudioDuration,
                EngineQueueing: Positive(recognized.QueueingDuration),
                AudioDecoding: Positive(recognized.DecodingDuration),
                RecordingReadable: recordingReadable,
                // What it cost to reach the engine at all, engine time removed. A large number
                // here means the work was waiting, not working.
                EngineTransport: dispatch is { } whole ? Positive(whole - recognized.ProcessingDuration) : null,
                EngineDispatch: Positive(recognized.EngineDispatchDuration),
                StreamedSegments: streamedSegmentCount)
            : null;

        // A long pause after a sentence marks a paragraph; the pipeline decides whether the app
        // the words are going to wants one. Guarded like every other stage: the words are
        // recognized, and a pipeline that throws must cost neither them nor the session.
        TextPipeline.RunResult run;
        try
        {
            run = pipeline(insertionTarget).Run(ParagraphBreaks.Mark(recognized.Text, recognized.Words));
        }
        catch (Exception error)
        {
            OnRecognitionFailure?.Invoke(error);
            DictationNotice failed = await PreserveForRetry(
                recording.Url,
                "The recognized text couldn't be prepared for insertion.").ConfigureAwait(false);
            await Report(take, EndingOwner.Finish, failed).ConfigureAwait(false);
            await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        TextPipeline.Output processed = run.Output;
        if (!RecognizedTextPolicy.CarriesWords(processed.Text) && RecognizedTextPolicy.CarriesWords(recognized.Text))
        {
            // Words were heard and cleanup left nothing: a take that was only a correction or
            // only hesitation. That is not a failure, so no alarm - and the take stays in
            // History with what was heard, so a take-back the person did not mean can be undone.
            Func<string, string, TargetApplication?, bool>? keep;
            lock (gate) { keep = OnTakeFinished; }

            _ = keep?.Invoke(recognized.Text, recording.Url, insertionTarget);
            await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
            await Report(take, EndingOwner.Finish, new DictationNotice(
                kind: DictationNoticeKind.Info,
                message: "Nothing left to insert after cleanup.",
                wordsDidNotLand: false)).ConfigureAwait(false);
            await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        if (!RecognizedTextPolicy.CarriesWords(processed.Text))
        {
            // An empty result is not an error: the person could have stayed silent, spoken too
            // quietly or used the wrong microphone. But one cannot stay silent in response: a
            // blank panel without text is indistinguishable from "inserted in the wrong place",
            // and the person goes looking for the missing phrase in someone else's window. A
            // press that is too short does not get here - it is filtered out above and needs no
            // explanation.
            //
            // The original asks only whether the text is empty, because its engine answers
            // silence with nothing. This one answers silence with punctuation, and a transcript
            // without a single letter or digit in it is the same "nothing was recognized" - see
            // RecognizedTextPolicy.
            await EndWithoutWords(recording.Url, heardOnlySilence, insertionTarget, take, token).ConfigureAwait(false);
            return;
        }

        await Insert(
            processed,
            run.Provenance,
            recognizedAt,
            microphoneStartup,
            phases,
            insertionTarget,
            take,
            token).ConfigureAwait(false);
        // The last moment the take still exists. Whoever keeps a history has to take its copy
        // now; the next line deletes the original, and that deletion is the promise that a
        // recording does not outlive its use.
        Func<string, string, TargetApplication?, bool>? takeFinished;
        lock (gate) { takeFinished = OnTakeFinished; }

        _ = takeFinished?.Invoke(processed.Text, recording.Url, insertionTarget);
        await Discard(take, EndingOwner.Finish, recording.Url).ConfigureAwait(false);
    }

    /// <summary>
    /// A take in which the model found no words. Never thrown away in silence.
    /// </summary>
    /// <remarks>
    /// Silence is not kept, and it is a warning: a muted, dead or wrong input needs the person.
    /// The microphone's name, when there is one, says where to look. Sound in which the model
    /// found nothing is kept for History: it may hold words the person still wants, and
    /// recognizing it again is one click. A take History does not keep is left where the
    /// launch-time import looks, never deleted here.
    ///
    /// <para>
    /// A take too long to hold in memory was recognized from its file, which is therefore whole
    /// and answers the same question.
    /// </para>
    /// </remarks>
    private async Task EndWithoutWords(
        string url,
        bool? heardOnlySilence,
        TargetApplication? target,
        Take take,
        CancellationToken token)
    {
        if (heardOnlySilence is null && readSamples is { } read)
        {
            try
            {
                float[] heard = await TranscriptionDeadline.WithTranscriptionDeadline(
                    recordingReadableDeadline,
                    workToken => read(url, workToken),
                    token).ConfigureAwait(false);
                heardOnlySilence = SilentTakePolicy.HeardOnlySilence(heard);
            }
            catch (Exception)
            {
                // A file that cannot be read back cannot prove silence; the take is kept.
            }
        }

        if (!ShouldContinue(take, token))
        {
            await Discard(take, EndingOwner.Finish, url).ConfigureAwait(false);
            await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        DictationNotice notice;
        if (heardOnlySilence == true)
        {
            await Discard(take, EndingOwner.Finish, url).ConfigureAwait(false);
            Func<string?>? microphoneName;
            lock (gate) { microphoneName = MicrophoneName; }

            string? name = microphoneName?.Invoke();
            notice = new DictationNotice(
                kind: DictationNoticeKind.Warning,
                message: name is null
                    ? "Nothing was recognized — nothing was inserted."
                    : $"Heard only silence from {name}.",
                wordsDidNotLand: true);
        }
        else
        {
            // Handed over here, and nothing waits between this and the notice, so the person is
            // told where the take went exactly when it got there.
            Func<string, string, TargetApplication?, bool>? takeFinished;
            lock (gate) { takeFinished = OnTakeFinished; }

            bool kept = takeFinished?.Invoke(string.Empty, url, target) == true;
            if (kept) { await Discard(take, EndingOwner.Finish, url).ConfigureAwait(false); }

            notice = new DictationNotice(
                kind: DictationNoticeKind.Info,
                message: kept
                    ? "Nothing was recognized — the recording is in History."
                    : "Nothing was recognized — nothing was inserted."
                        + " The local take will be checked for automatic recovery on the next launch.",
                wordsDidNotLand: true);
        }

        await Report(take, EndingOwner.Finish, notice).ConfigureAwait(false);
        await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
    }

    /// <summary>
    /// Put a streamed take together: the pieces already recognized, plus the tail nobody has
    /// looked at yet.
    ///
    /// The tail is the only decode the person actually waits for, which is the whole point of
    /// the exercise. It is also the one piece that can be too short to trust on its own - when
    /// the key comes up just after a cut - and in that case the segment before it is taken back
    /// and the two are recognized together rather than shipping a fragment.
    /// </summary>
    private static async Task<ASRResult> JoinStreamed(
        IReadOnlyList<string> texts,
        IReadOnlyList<IReadOnlyList<ASRResult.Word>> segmentWords,
        IReadOnlyList<int> segmentSampleCounts,
        int consumedSamples,
        float[] samples,
        Func<float[], CancellationToken, Task<ASRResult>> transcribe,
        CancellationToken token,
        TimeSpan decodingDuration = default)
    {
        // A stream whose counts do not add up is not a prefix of this take, and joining it
        // would publish a transcript with a hole or a repeat in it.
        if (consumedSamples < 0
            || consumedSamples > samples.Length
            || texts.Count != segmentSampleCounts.Count
            || segmentSampleCounts.Any(static count => count <= 0)
            || segmentSampleCounts.Sum() != consumedSamples)
        {
            throw new ASREngineError.UnsupportedAudioFormat("incomplete streamed recording");
        }

        var pieces = new List<string>(texts);
        int tailStart = Math.Min(Math.Max(0, consumedSamples), samples.Length);

        if (samples.Length - tailStart < MinimumTailSamples
            && pieces.Count == segmentSampleCounts.Count
            && segmentSampleCounts.Count > 0)
        {
            pieces.RemoveAt(pieces.Count - 1);
            tailStart = Math.Max(0, tailStart - segmentSampleCounts[^1]);
        }

        // Word timings on the take's own clock, for paragraph breaks. Kept only while every
        // piece with text brought its words; otherwise none, because a partial list cannot be
        // matched to the text.
        List<ASRResult.Word>? words = segmentWords.Count == texts.Count ? [] : null;
        int offset = 0;
        for (int index = 0; index < pieces.Count; index += 1)
        {
            IReadOnlyList<ASRResult.Word> pieceWords = index < segmentWords.Count ? segmentWords[index] : [];
            if (pieces[index].Trim().Length != 0 && pieceWords.Count == 0) { words = null; }
            words?.AddRange(Shift(pieceWords, offset));
            offset += segmentSampleCounts[index];
        }

        TimeSpan processing = TimeSpan.Zero;
        TimeSpan dispatch = TimeSpan.Zero;
        float[] tail = samples[tailStart..];
        if (tail.Length > 0)
        {
            ASRResult result = await transcribe(tail, token).ConfigureAwait(false);
            pieces.Add(result.Text);
            if (result.Text.Trim().Length != 0 && result.Words.Count == 0) { words = null; }
            words?.AddRange(Shift(result.Words, tailStart));
            processing = result.ProcessingDuration;
            dispatch = result.EngineDispatchDuration;
        }

        string text = string.Join(
            ' ',
            pieces.Select(static piece => piece.Trim()).Where(static piece => piece.Length > 0));

        // The durations describe the tail, and that is deliberate: they are what the person
        // waited for after letting go of the key. Reporting the sum of every segment would be
        // arithmetically true and describe a wait nobody had.
        return new ASRResult(
            text,
            words: words ?? [],
            audioDuration: TimeSpan.FromSeconds((double)samples.Length / SampleRate),
            processingDuration: processing,
            engineDispatchDuration: dispatch,
            decodingDuration: decodingDuration);
    }

    /// <summary>Move a piece's word timings onto the take's clock.</summary>
    private static IEnumerable<ASRResult.Word> Shift(IReadOnlyList<ASRResult.Word> words, int samples)
    {
        TimeSpan seconds = TimeSpan.FromSeconds((double)samples / SampleRate);
        return words.Select(word => word with { Start = word.Start + seconds, End = word.End + seconds });
    }

    /// <summary>A stage that did not run is absent, never zero. Zero is a measurement.</summary>
    private static TimeSpan? Positive(TimeSpan value) => value > TimeSpan.Zero ? value : null;

    /// <summary>
    /// The capture never closed its take. Let go of it and free the session.
    /// </summary>
    /// <remarks>
    /// The abort is dispatched and not awaited on purpose: whatever wedged the stop is very
    /// likely to wedge the abort as well, and waiting for it would reintroduce the very hang
    /// this path exists to end. What it buys when it does return is the microphone: nothing else
    /// will ever close it, because the session that owned it is gone by then.
    ///
    /// <para>
    /// There is no take to keep: the WAV was never closed, so its header still says zero. The
    /// import at the next launch is what repairs and recovers such a file - the same path that
    /// already handles a recording left behind by a crash.
    /// </para>
    /// </remarks>
    private async Task AbandonWedgedCapture(Take take, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await capture.AbortRecording(take.Session).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing left to tell: the session it belonged to has already been ended below.
            }
        });

        if (!ShouldContinue(take, token))
        {
            await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        await Report(take, EndingOwner.Finish, new DictationNotice(
            kind: DictationNoticeKind.Failure,
            message: "The microphone didn't stop when the key was released, so this dictation was "
                + "dropped. Restarting OpenRamble usually fixes this.",
            wordsDidNotLand: true)).ConfigureAwait(false);
        await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
    }

    /// <summary>
    /// Collect a speed report and send it outside.
    ///
    /// Without the release mark there is no report at all: there is nothing to
    /// count "stop → text" from, and showing a number computed from something
    /// else is a lie.
    /// </summary>
    private void ReportSpeed(
        Take take,
        MonotonicInstant recognizedAt,
        InsertionMarks marks,
        TimeSpan? microphoneStartup,
        DictationPhaseBreakdown? phases)
    {
        if (OnSpeed is not { } onSpeed || take.StopRequestedAt is not { } releasedAt) { return; }

        onSpeed(new DictationSpeedReport(
            releasedAt.DurationTo(recognizedAt),
            marks.PasteDispatchedAt is { } paste ? releasedAt.DurationTo(paste) : null,
            marks.ClipboardRestoredAt is { } restored ? releasedAt.DurationTo(restored) : null,
            microphoneStartup,
            phases));
    }

    private async Task Insert(
        TextPipeline.Output output,
        PipelineProvenance provenance,
        MonotonicInstant recognizedAt,
        TimeSpan? microphoneStartup,
        DictationPhaseBreakdown? phases,
        TargetApplication? target,
        Take take,
        CancellationToken token)
    {
        lock (gate)
        {
            // Escape may have taken the ending over; from here on it cannot.
            if (!take.Owns(EndingOwner.Finish)) { return; }

            SetState(DictationState.Inserting);
        }

        // Cmd+V is the meaningful completion feedback. Do not leave a redundant
        // "Inserting" panel over the destination application while the clipboard
        // is restored in the background.
        await overlay.Dismiss().ConfigureAwait(false);

        // Last point where cancellation is still possible. Then the event goes
        // into someone else's application and does not respond.
        if (!ShouldContinue(take, token))
        {
            await FinishWithoutInsertion(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        InsertionMarks marks;
        try
        {
            marks = await TranscriptionDeadline.WithTranscriptionDeadline(
                insertionDeadline,
                _ => inserter.InsertReportingMarks(output.Text, target)).ConfigureAwait(false);
        }
        catch (TranscriptionTimeout)
        {
            if (!ShouldContinue(take, token)) { return; }

            await HandleInsertionTimeout(output.Text, take).ConfigureAwait(false);
            return;
        }
        catch (Exception error)
        {
            if (!ShouldContinue(take, token)) { return; }

            await HandleInsertionFailure(error, output.Text, take).ConfigureAwait(false);
            return;
        }

        // Paste may already be irreversible, but a late completion from an old session must not
        // mutate callbacks, history, UI, or the next session.
        if (!ShouldContinue(take, token)) { return; }

        lock (gate)
        {
            OnTextInserted?.Invoke(output.Text);
            // Origin - only after a successful insertion: an unsuccessful one has
            // no "what the person saw" and nothing to give to "copy verbatim".
            OnDictationCompleted?.Invoke(provenance);
            ReportSpeed(take, recognizedAt, marks, microphoneStartup, phases);
        }

        // The keypress is handled separately from the insertion intentionally.
        // These are different system calls, and the second one fails while the
        // first one is alive - for example, when the user never released the modifier.
        if (output.Command == TrailingCommand.PressReturn)
        {
            try
            {
                await TranscriptionDeadline.WithTranscriptionDeadline(
                    returnDeadline,
                    async _ =>
                    {
                        await inserter.PressReturn(target).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (!ShouldContinue(take, token)) { return; }

                await ReportReturnFailure(take).ConfigureAwait(false);
                return;
            }

            if (!ShouldContinue(take, token)) { return; }
        }

        await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
    }

    /// <summary>
    /// The system edge did not return, so we cannot truthfully claim either success or failure:
    /// a synchronous input post may have crossed its irreversible boundary before the watchdog
    /// fired. Keep a copy and tell the person exactly that, while freeing the next dictation
    /// immediately.
    /// </summary>
    private async Task HandleInsertionTimeout(string text, Take take)
    {
        lock (gate) { pendingRecovery = new RecoveredDictation(text); }

        await Report(take, EndingOwner.Finish, new DictationNotice(
            kind: DictationNoticeKind.Warning,
            message: "Insertion couldn't be confirmed in time. The text is saved in the menu and may already have been pasted.",
            recoverableText: text,
            wordsDidNotLand: true)).ConfigureAwait(false);
        await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
    }

    /// <summary>
    /// The text was inserted, but pressing Return failed.
    ///
    /// The general failure path would lie twice here: it would say "the text is
    /// not inserted" when it is in place, and would save a second copy of the
    /// dictation to disk. A private tool does not pile up what was said without reason.
    /// </summary>
    private async Task ReportReturnFailure(Take take)
    {
        await Report(take, EndingOwner.Finish, new DictationNotice(
            kind: DictationNoticeKind.Warning,
            message: "The text was inserted, but pressing Return failed.")).ConfigureAwait(false);
        await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
    }

    /// <summary>The text was recognized, but could not be inserted - we keep it so that it does not disappear.</summary>
    private async Task HandleInsertionFailure(Exception error, string text, Take take)
    {
        if (error is TextInsertionError.InsertedButClipboardRestoreFailed)
        {
            await Report(take, EndingOwner.Finish, new DictationNotice(
                kind: DictationNoticeKind.Warning,
                message: "The text was inserted, but the previous clipboard couldn't be restored."))
                .ConfigureAwait(false);
            await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
            return;
        }

        lock (gate) { pendingRecovery = new RecoveredDictation(text); }

        // Not a failure, but a normal situation: the password field is active.
        string message = error is TextInsertionError.SecureInputActive
            ? "Text not inserted: secure input is active. Your text is saved in the menu."
            : "The text couldn't be inserted. It's saved in the menu.";

        await Report(take, EndingOwner.Finish, new DictationNotice(
            kind: DictationNoticeKind.Warning,
            message: message,
            recoverableText: text,
            wordsDidNotLand: true)).ConfigureAwait(false);
        await Cleanup(take, EndingOwner.Finish).ConfigureAwait(false);
    }

    // MARK: - Cancel

    /// <summary>Cancel dictation.</summary>
    public void Cancel()
    {
        Take? cancelled;
        CancellationTokenSource? running;
        lock (gate)
        {
            cancelled = take;
            if (cancelled is null || !DictationStopPolicy.CanCancel(state)) { return; }

            // The order is important: first the claim, then cancel the task.
            // Cancelling a task does not interrupt an already ongoing wait, and
            // the ownership is checked after each of them. A stop, a watchdog or a rescue
            // already under way hands its ending over: from here on it speaks for nothing.
            if (!cancelled.TryClaimEnding(EndingOwner.Cancel)) { return; }

            running = cancelled.Finalization;
        }

        running?.Cancel();
        // Segments still queued belong to a take nobody will insert; left running they would
        // hold the engine against the next dictation.
        cancelled.Streamed?.Cancel();

        // Device containment continues in the background. It must never own the controller's
        // return to idle: a cancellation-deaf device edge cannot hold the panel.
        _ = Task.Run(async () =>
        {
            await AbortWithinGrace(cancelled).ConfigureAwait(false);
            await FinishWithoutInsertion(cancelled, EndingOwner.Cancel).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Start closing the microphone, and wait for it only as long as a healthy device needs.
    /// </summary>
    /// <remarks>
    /// The abort itself keeps running past the grace; what is abandoned is the wait for it, not
    /// the closing. Its eventual failure has nobody to tell: the session it belonged to is
    /// ended by the caller either way.
    /// </remarks>
    private async Task AbortWithinGrace(Take take)
    {
        Task abort;
        try
        {
            abort = capture.AbortRecording(take.Session);
        }
        catch (Exception)
        {
            return;
        }

        _ = abort.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await Task.WhenAny(abort, Task.Delay(AbortForegroundGrace)).ConfigureAwait(false);
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
        int? session;
        lock (gate) { session = take?.Session; }

        if (session is { } current) { Interrupt(current, reason); }
    }

    /// <summary>
    /// The recording of <paramref name="session"/> failed on its own. A failure of any other
    /// session's take - one that is already over, reporting late - ends nothing.
    /// </summary>
    public void Interrupt(int session, string reason)
    {
        Take? interrupted;
        lock (gate)
        {
            interrupted = take;
            // Only a take still preparing or listening: one already ending has its own outcome.
            if (interrupted?.Session != session || !interrupted.TryClaimEnding(EndingOwner.Interrupt)) { return; }
        }

        var notice = new DictationNotice(kind: DictationNoticeKind.Failure, message: reason);
        _ = Task.Run(async () =>
        {
            // Through Report: the interruption loses the words, and the person
            // mid-sentence learns of it by ear like any other loss.
            await Report(interrupted, EndingOwner.Interrupt, notice).ConfigureAwait(false);
            await AbortWithinGrace(interrupted).ConfigureAwait(false);
            await FinishWithoutInsertion(interrupted, EndingOwner.Interrupt).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// System permission or device disappeared while recording.
    /// Close the WAV and keep it for an explicit Retry/Delete without trying to
    /// recognize or insert into an already untrusted system state.
    /// </summary>
    public void PreserveActiveRecording(string reason)
    {
        Take? preserved;
        lock (gate)
        {
            preserved = take;
            if (preserved is null) { return; }

            // A listening take has a recording to keep.
            if (preserved.TryClaimEnding(EndingOwner.Rescue))
            {
                SetState(DictationState.Transcribing);
                preserved.Finalization = new CancellationTokenSource();
                CancellationToken token = preserved.Finalization.Token;
                _ = Task.Run(() => Rescue(reason, preserved, token));
                return;
            }

            if (preserved.Phase != TakePhase.Preparing) { return; }
        }

        // A take still preparing has recorded nothing to keep. It ends as an interrupted one,
        // with the reason as its notice - through Report, so the notice carries the attention
        // sound like every other surfaced failure.
        Interrupt(preserved.Session, reason);
    }

    private async Task Rescue(string reason, Take take, CancellationToken token)
    {
        (string Url, TimeSpan Duration)? recording;
        try
        {
            // Under the same budget as the ordinary stop, and here it matters most: this path runs
            // because the device or the permission just disappeared, which is precisely when a
            // stop is likely to be a call into something that no longer answers.
            recording = await TranscriptionDeadline.WithTranscriptionDeadline(
                captureStopDeadline,
                _ => capture.StopRecording(take.Session)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            recording = null;
            // Nothing else will ever close the microphone: the stop that owned it is abandoned.
            _ = Task.Run(async () =>
            {
                try
                {
                    await capture.AbortRecording(take.Session).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The session is ended below either way.
                }
            });
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
            if (recording is { } cancelled) { await Discard(take, EndingOwner.Rescue, cancelled.Url).ConfigureAwait(false); }

            await Cleanup(take, EndingOwner.Rescue).ConfigureAwait(false);
            return;
        }

        DictationNotice notice;
        if (recording is { } fragment && !DictationDurationPolicy.IsWorthTranscribing(fragment.Duration))
        {
            // The device vanished a moment after the start: the take holds not a
            // single word, there is nothing to rescue. The main path deletes such
            // fragments silently - here we name the reason of the interruption
            // and nothing else, otherwise a one-second device hiccup leaves a
            // "recording for retry" whose retry can only ever produce an empty result.
            await Discard(take, EndingOwner.Rescue, fragment.Url).ConfigureAwait(false);
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

        await Report(take, EndingOwner.Rescue, notice).ConfigureAwait(false);
        await Cleanup(take, EndingOwner.Rescue).ConfigureAwait(false);
    }

    // MARK: - Completion

    /// <summary>Is the take for which the wait began still ongoing? The gate must be held.</summary>
    private bool IsCurrent(Take waiting) => ReferenceEquals(take, waiting);

    /// <summary>Whether the stop still owns the ending of its take and should carry it on.</summary>
    private bool ShouldContinue(Take waiting, CancellationToken token)
    {
        lock (gate)
        {
            if (!IsCurrent(waiting)) { return false; }

            return DictationFinalizationPolicy.ShouldContinue(
                state,
                cancellationRequested: !waiting.Owns(EndingOwner.Finish),
                token.IsCancellationRequested);
        }
    }

    /// <summary>
    /// There is no reason to show the failure of a cancelled session: the person has already
    /// closed it, and the message would fall on top of the one that is going on now. Report and
    /// Cleanup speak only for the owner of the ending, so a failure caused by Escape's own abort
    /// stays silent.
    /// </summary>
    private async Task Fail(Take failed, EndingOwner owner, DictationError error)
    {
        await Report(failed, owner, new DictationNotice(
            kind: DictationNoticeKind.Failure,
            message: error.UserMessage)).ConfigureAwait(false);
        await Cleanup(failed, owner).ConfigureAwait(false);
    }

    private Task FinishWithoutInsertion(Take finished, EndingOwner owner) => Cleanup(finished, owner);

    /// <summary>
    /// Cleaning after the session, in strict order.
    ///
    /// The microphone is muted here and only here: the promise "the recording
    /// indicator is not lit while we are not listening" relies on this method
    /// being called on every completion path, including errors and cancellation.
    ///
    /// You can only clean up your own session: the tail of a previous one, woken
    /// after a cancellation, would otherwise put out the new one already in progress.
    /// And only the path that owns the ending cleans up, exactly once: a second cleanup's
    /// dismiss could land on the next take's panel.
    /// </summary>
    private async Task Cleanup(Take finished, EndingOwner owner)
    {
        lock (gate)
        {
            // From here the ending is committed: nothing can take it over any more.
            if (!finished.TryCompleteEnding(owner)) { return; }
        }

        // The terminal panel command goes out while this session is still current, before any
        // externally observable idle state: an idle observer is allowed to start the next
        // session synchronously, and a dismiss issued after that would take its panel away.
        await overlay.Dismiss().ConfigureAwait(false);

        lock (gate)
        {
            // Only the completed take's own cleanup gets here, and no take can begin before it
            // sets Idle below: the field still holds this take.
            // The whole session state goes with its take: the release mark, the hands-free mode,
            // the target, the stream, the sound already played. Nothing of it can flow into the
            // next one, and nothing still holding this take can reach the next one through it.
            take = null;
            SetHandsFreeActive(false);
            isAnnouncingEngineWait = false;
            SetState(DictationState.Idle);
        }

        // Whatever the stream still had queued can never publish into the next take.
        finished.Streamed?.Cancel();
    }

    /// <summary>The gate must be held.</summary>
    private TimeSpan ElapsedSeconds(Take take) =>
        take.RecordingStartedAt is { } startedAt ? now() - startedAt : TimeSpan.Zero;

    /// <summary>
    /// Show the message: both to the subscriber and on the panel.
    ///
    /// The only way messages exit from the kernel - the attention sound rides on
    /// it, so a path around <see cref="Report"/> is a path around the ear.
    /// </summary>
    /// <param name="owner">
    /// The path speaking. Only the owner of the take's ending speaks for it - before its cleanup
    /// and after, as a recording that could not be deleted still needs saying.
    /// </param>
    private async Task Report(Take take, EndingOwner owner, DictationNotice notice)
    {
        bool sound;
        lock (gate)
        {
            if (take.Owner != owner) { return; }

            OnNotice?.Invoke(notice);
            // One sound per session, and only for words that never landed.
            //
            // "The text was inserted, but Return failed" is shown, not sounded:
            // the words are in the field, the person is looking at them. The ear
            // is reserved for the loss they would otherwise miss.
            sound = notice.WordsDidNotLand && !take.HasSounded;
            if (sound) { take.HasSounded = true; }
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
    private async Task Discard(Take take, EndingOwner owner, string url)
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
            await Report(take, owner, DictationNotice.ForException(
                DictationNoticeKind.Failure,
                "Couldn't delete the local recording",
                failure)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Everything one take owns, from its key-down to its cleanup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each continuation of a take - the capture start, the finalization, a watchdog, a rescue -
    /// carries this object and acts on it, never on a field of the controller. A continuation can
    /// resume after its take is over and the next one has begun: the field would then hold the
    /// next take's stream, target and marks, and an old finalization would finish the new
    /// stream, an old notice would sound on behalf of the new take. Identity is the object
    /// itself; the cleanup lets go of it, after which nothing holding it is current.
    /// </para>
    /// <para>
    /// The mutable members are guarded by the controller's gate, like the rest of its state. The
    /// fallback error has a lock of its own, because recognition work abandoned at its deadline
    /// may still try to write it.
    /// </para>
    /// </remarks>
    /// <summary>The life of one take: Preparing → Listening → Ending(owner) → Done.</summary>
    /// <remarks>
    /// Preparing may go straight to Ending when the microphone never opens or the take is ended
    /// before it does. The controller-wide <see cref="DictationState"/> is what the person sees;
    /// this is who may still act on the take.
    /// </remarks>
    private enum TakePhase
    {
        Preparing,
        Listening,
        Ending,
        Done,
    }

    /// <summary>Which path claimed the end of a take.</summary>
    private enum EndingOwner
    {
        /// <summary>The release: recognition and insertion.</summary>
        Finish,

        /// <summary>The microphone did not open in time.</summary>
        Watchdog,

        /// <summary>The device or the permission went away mid-take; the recording is kept.</summary>
        Rescue,

        /// <summary>Escape.</summary>
        Cancel,

        /// <summary>The recording failed on its own, including a start that threw.</summary>
        Interrupt,
    }

    private sealed class Take
    {
        private readonly TaskCompletionSource<TargetApplication?> target =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Lock fallbackGate = new();
        private Exception? fallbackError;
        private bool fallbackSealed;

        public Take(int session, MonotonicInstant beganAt, StreamedSegmentRecognizer? streamed)
        {
            Session = session;
            BeganAt = beganAt;
            Streamed = streamed;
        }

        /// <summary>The number the capture and the log know this take by.</summary>
        public int Session { get; }

        /// <summary>The key-down, on the real clock the watchdog runs on.</summary>
        public MonotonicInstant BeganAt { get; }

        /// <summary>
        /// Recognizes the take in pieces while it is still being spoken. Absent means the old
        /// behaviour exactly: one decode of the whole take once the key comes up.
        /// </summary>
        public StreamedSegmentRecognizer? Streamed { get; }

        /// <summary>
        /// The application the words go to, as it was at the key-down. Asked for then and
        /// awaited only when the words are about to be shaped for it.
        /// </summary>
        public Task<TargetApplication?> Target => target.Task;

        /// <summary>Whether recording goes on without holding the key.</summary>
        public bool IsHandsFree { get; set; }

        /// <summary>
        /// A key release that occurred before the recording began. Processed exactly once, when
        /// the microphone opens; a lost flag leaves recording enabled.
        /// </summary>
        public bool DeferredStopRequested { get; set; }

        /// <summary>Whether this take has already asked for the person's attention.</summary>
        public bool HasSounded { get; set; }

        /// <summary>
        /// The Swift original keeps the finalization <c>Task</c> to cancel it; here the
        /// cancellation source alone carries that meaning. Present once a stop or a rescue owns
        /// the ending; a cancellation that takes the ending over trips it. Who owns the ending is
        /// <see cref="Owner"/>, not this.
        /// </summary>
        /// <remarks>
        /// Deliberately never disposed: cancellation registrations of an abandoned
        /// transcription outlive the session that started it.
        /// </remarks>
        public CancellationTokenSource? Finalization { get; set; }

        /// <summary>
        /// Where the take is. Moves only forward and only under the controller's gate. A take
        /// that is not <see cref="TakePhase.Done"/> is the controller's current one.
        /// </summary>
        public TakePhase Phase { get; private set; } = TakePhase.Preparing;

        /// <summary>
        /// The path that ends the take, from the moment it claimed the ending. It alone speaks
        /// for the take and cleans it up.
        /// </summary>
        public EndingOwner? Owner { get; private set; }

        /// <summary>The one watchdog that ends this take's microphone if it never starts.</summary>
        public CancellationTokenSource? MicrophoneStartWatchdog { get; set; }

        public MonotonicInstant? MicrophoneStartWatchdogDeadline { get; set; }

        /// <summary>The gate must be held. The microphone opened: Preparing → Listening.</summary>
        public bool TryStartListening()
        {
            if (Phase != TakePhase.Preparing) { return false; }

            Phase = TakePhase.Listening;
            CancelMicrophoneStartWatchdog();
            return true;
        }

        /// <summary>
        /// The gate must be held. Claim the ending for <paramref name="claimant"/>, if this phase
        /// lets it: a stop and a rescue need a listening take, the watchdog a preparing one, and
        /// a cancellation may take over a stop, a rescue or a watchdog still under way - Escape is
        /// what ends a wedge.
        /// </summary>
        public bool TryClaimEnding(EndingOwner claimant)
        {
            bool allowed = (Phase, claimant) switch
            {
                (TakePhase.Preparing, EndingOwner.Watchdog or EndingOwner.Cancel or EndingOwner.Interrupt) => true,
                (TakePhase.Listening, EndingOwner.Finish or EndingOwner.Rescue or EndingOwner.Cancel or EndingOwner.Interrupt) => true,
                (TakePhase.Ending, EndingOwner.Cancel) => Owner is EndingOwner.Finish or EndingOwner.Rescue or EndingOwner.Watchdog,
                _ => false,
            };
            if (!allowed) { return false; }

            Phase = TakePhase.Ending;
            Owner = claimant;
            CancelMicrophoneStartWatchdog();
            return true;
        }

        /// <summary>The gate must be held. Whether <paramref name="owner"/> is ending the take right now.</summary>
        public bool Owns(EndingOwner owner) => Phase == TakePhase.Ending && Owner == owner;

        /// <summary>The gate must be held. The owner's cleanup begins: Ending → Done.</summary>
        public bool TryCompleteEnding(EndingOwner owner)
        {
            if (!Owns(owner)) { return false; }

            Phase = TakePhase.Done;
            return true;
        }

        /// <summary>The gate must be held.</summary>
        public void CancelMicrophoneStartWatchdog()
        {
            MicrophoneStartWatchdog?.Cancel();
            MicrophoneStartWatchdog = null;
            MicrophoneStartWatchdogDeadline = null;
        }

        public DateTimeOffset? RecordingStartedAt { get; set; }

        /// <summary>The moment the key is released is the start of the "stop → text" countdown.</summary>
        public MonotonicInstant? StopRequestedAt { get; set; }

        /// <summary>Ask for the target off the caller's thread. A query that throws means "unknown".</summary>
        public void ResolveTarget(Func<TargetApplication?> snapshot) =>
            _ = Task.Run(() =>
            {
                try
                {
                    target.TrySetResult(snapshot());
                }
                catch (Exception)
                {
                    target.TrySetResult(null);
                }
            });

        /// <summary>Why the take had to be recognized whole again. Ignored once sealed.</summary>
        public void RecordFallback(Exception error)
        {
            lock (fallbackGate)
            {
                if (!fallbackSealed) { fallbackError ??= error; }
            }
        }

        /// <summary>
        /// End the recognition stage: hand over the fallback error once, and refuse any later one.
        /// </summary>
        public Exception? SealFallback()
        {
            lock (fallbackGate)
            {
                fallbackSealed = true;
                Exception? error = fallbackError;
                fallbackError = null;
                return error;
            }
        }
    }
}

/// <summary>
/// Preparation exceeded the foreground stop-to-result budget. This is not an inference stall: a
/// separately owned model load may still be healthy and must not be killed just as it is about
/// to become ready.
/// </summary>
internal sealed class DictationPreparationTimeout : Exception
{
    public DictationPreparationTimeout()
        : base("the engine did not become ready within the foreground budget")
    {
    }
}

/// <summary>Where a take failed, for the local journal. A case name, never an error description.</summary>
public enum DictationFailureStage
{
    CaptureFreeze,
    ModelPreparation,
    Recognition,
}

/// <summary>Why a take had to be recognized whole again, for the local journal.</summary>
public enum DictationFallbackReason
{
    SegmentFailed,
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
