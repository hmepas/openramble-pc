using System.Diagnostics;

namespace DictationCore;

/// <summary>
/// A point on the monotonic clock - the port of Swift's <c>ContinuousClock.Instant</c>.
///
/// Wall clocks are deliberately not used for the speed report: sleep, daylight saving
/// or an NTP step in the middle of a dictation would turn "stop → text" negative,
/// that is, into a lie.
/// </summary>
/// <param name="Timestamp">A raw <see cref="Stopwatch"/> timestamp, not a <see cref="TimeSpan"/> tick count.</param>
public readonly record struct MonotonicInstant(long Timestamp)
{
    public static MonotonicInstant Now => new(Stopwatch.GetTimestamp());

    /// <summary>How much time passes from this instant to <paramref name="other"/>.</summary>
    public TimeSpan DurationTo(MonotonicInstant other) =>
        Stopwatch.GetElapsedTime(Timestamp, other.Timestamp);

    /// <summary>The instant <paramref name="duration"/> later than this one.</summary>
    /// <remarks>
    /// Rounded, not truncated: 120 ms is 119999999.99… stopwatch ticks in binary
    /// floating point, and truncating would make the instant land a tick early -
    /// a scripted "+120 ms" would come back as 119.9999 ms.
    /// </remarks>
    public MonotonicInstant Advanced(TimeSpan duration) =>
        new(Timestamp + (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency));
}

/// <summary>
/// How long it took from releasing the key to each noticeable point.
///
/// There are three marks, not one, because "fast" is three different promises, and
/// it is unfair to confuse them:
///
/// <list type="bullet">
/// <item><description><c>ToRecognizedText</c> - the engine returned the text;</description></item>
/// <item><description><c>ToPasteDispatched</c> - paste sent, that is, the text is already in someone else's window.
/// <b>This is the headline metric</b> and what the HUD shows;</description></item>
/// <item><description><c>ToClipboardRestored</c> - the previous contents of the buffer are returned to their place.</description></item>
/// </list>
///
/// There is no forbidden metric here intentionally: "time until the text inserter
/// returns" is not a speed - it is a thousand milliseconds inside
/// clipboard protection. It is waiting, not working.
///
/// <c>null</c> means "not measured", and never "zero". An edge that cannot
/// give a mark has no right to look instant.
/// </summary>
/// <param name="MicrophoneStartup">
/// How long the microphone was coming up before the first frame. It is not counted from
/// the release - it is a warm-up before the phrase, not after it.
/// </param>
/// <param name="Phases">
/// Where <paramref name="ToRecognizedText"/> actually went. <c>null</c> only for a report built
/// without the breakdown; a stage that did not run is absent inside it.
/// </param>
public sealed record DictationSpeedReport(
    TimeSpan ToRecognizedText,
    TimeSpan? ToPasteDispatched = null,
    TimeSpan? ToClipboardRestored = null,
    TimeSpan? MicrophoneStartup = null,
    DictationPhaseBreakdown? Phases = null);

/// <summary>
/// Which causal stage of a finished take owned the wait.
///
/// A single "stop → text" number cannot tell a slow engine from a model that was not resident,
/// and it cannot tell either of them from a machine that paged the engine out between two takes.
/// Those are different bugs with different fixes, so the stages are measured apart.
///
/// The first three are consecutive and sum to <c>ToRecognizedText</c>. <see cref="EngineProcessing"/>
/// is not one of them: it is the same interval as <see cref="Recognition"/> seen from inside the
/// engine, so <see cref="Recognition"/> minus <see cref="EngineProcessing"/> is everything
/// recognition costs that is not recognition - transport, scheduling, and page faults.
///
/// A stage that did not run is <c>null</c> and never zero. Zero is a measurement.
/// </summary>
/// <remarks>
/// The Swift original also carries four executor-hop measurements (<c>executorHandover</c>,
/// <c>poolReturn</c>, <c>mainActorReturn</c>, <c>returnFrameWasMainThread</c>). They describe
/// Swift's cooperative pool and its main actor and have no .NET counterpart; they are not ported.
/// </remarks>
/// <param name="CaptureFreeze">Key-up → the capture handed its PCM over.</param>
/// <param name="EnginePreparation">
/// The foreground wait for a model that was not resident when the person stopped speaking.
/// <c>null</c> when this build has no preparation hook at all; a near-zero value means the
/// engine was already there.
/// </param>
/// <param name="Recognition">The recognition call as the application measured it, transport included.</param>
/// <param name="EngineProcessing">The same call as the engine reported it, transport excluded.</param>
/// <param name="EngineQueueing">
/// How long the take waited before the engine started on it.
///
/// The stage that was missing. <see cref="Recognition"/> minus <see cref="EngineProcessing"/>
/// used to be a single unattributed lump, and every stall this app has had lived inside it - a
/// blocked thread pool, a doubled inference, a disk. Naming it means the next one identifies
/// itself instead of needing a sampler.
/// </param>
/// <param name="AudioDecoding">
/// Opening and decoding the recording, which is where an eleven-second dictation with a tenth of
/// a second of inference actually went.
/// </param>
/// <param name="RecordingReadable">
/// Waiting for the recording to finish being written and closed before anything could read it.
/// </param>
/// <param name="EngineTransport">
/// Everything between dispatching the recognition call and the engine starting work - hops,
/// waiting for a thread. What it cost to reach the engine at all, engine time removed. A large
/// number here means the work was waiting, not working.
/// </param>
/// <param name="EngineDispatch">Admission to and return from the engine's own serial execution queue.</param>
/// <param name="AudioDuration">How much audio the take actually carried.</param>
/// <param name="StreamedSegments">
/// How many pieces were recognized while the person was still speaking.
///
/// Zero means the take went to the engine whole, which is what a short one does and what any
/// take with no pause in it does. Without this number the log cannot tell a fast take from a
/// streamed one, and those want different explanations.
/// </param>
public sealed record DictationPhaseBreakdown(
    TimeSpan CaptureFreeze,
    TimeSpan? EnginePreparation,
    TimeSpan Recognition,
    TimeSpan? EngineProcessing,
    TimeSpan AudioDuration,
    TimeSpan? EngineQueueing = null,
    TimeSpan? AudioDecoding = null,
    TimeSpan? RecordingReadable = null,
    TimeSpan? EngineTransport = null,
    TimeSpan? EngineDispatch = null,
    int StreamedSegments = 0);

/// <summary>
/// When the insertion has done something that is visible to a human.
///
/// The inserter itself takes the marks: they cannot be taken from outside, because insertion
/// returns only after a second of clipboard protection.
/// </summary>
public sealed record InsertionMarks(
    MonotonicInstant? PasteDispatchedAt = null,
    MonotonicInstant? ClipboardRestoredAt = null);
