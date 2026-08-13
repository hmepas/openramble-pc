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
public sealed record DictationSpeedReport(
    TimeSpan ToRecognizedText,
    TimeSpan? ToPasteDispatched = null,
    TimeSpan? ToClipboardRestored = null,
    TimeSpan? MicrophoneStartup = null);

/// <summary>
/// When the insertion has done something that is visible to a human.
///
/// The inserter itself takes the marks: they cannot be taken from outside, because insertion
/// returns only after a second of clipboard protection.
/// </summary>
public sealed record InsertionMarks(
    MonotonicInstant? PasteDispatchedAt = null,
    MonotonicInstant? ClipboardRestoredAt = null);
