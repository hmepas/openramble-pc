namespace DictationCore;

/// <summary>
/// How long recognition may run before the session declares it wedged.
///
/// The engine normally runs many times faster than real time, but it sits on
/// system services that can die under it - a prediction stuck on a
/// restarted daemon blocks its await forever, and the person stares at
/// "Transcribing…" with no way out but Escape, which throws their words away.
/// The deadline converts that infinite hang into a bounded failure that keeps
/// the recording.
/// </summary>
public static class TranscriptionDeadline
{
    /// <summary>
    /// This is a backstop against a dead system service, not a performance
    /// budget - and the difference is the whole point.
    ///
    /// It used to be three seconds, derived from a warm recognizer running
    /// over 100x real time. That reasoning measured the best case and then
    /// spent it: a take that ran slowly for an ordinary reason - the machine
    /// paging the model back in, another application holding the accelerator,
    /// a machine with 8 GB of memory - blew a budget calibrated on the good day,
    /// and the words were withheld from someone whose engine was working
    /// perfectly. Worse, the failure destroyed the loaded model, so the next
    /// take started cold and was even more likely to miss the same bound. A
    /// slow machine got a spiral instead of a slow result.
    ///
    /// Wedge detection belongs to the watchdog that can see whether the
    /// recognizer is burning CPU; time alone cannot tell a slow machine from a
    /// broken one. What remains here is a far ceiling, generous enough that no
    /// healthy machine can reach it and small enough that a truly dead call
    /// still ends in a recoverable failure rather than an eternal panel.
    /// </summary>
    public static TimeSpan Deadline(TimeSpan audioDuration) =>
        TimeSpan.FromSeconds(Math.Max(120, audioDuration.TotalSeconds * 2));

    /// <summary>
    /// Race an operation against a deadline without inheriting its fate.
    ///
    /// Awaiting the operation itself would wait for a call that is stuck on a dead
    /// service and ignoring cancellation - exactly what must not happen. The loser
    /// here is cancelled best-effort through its token and abandoned: it finishes
    /// into the void whenever the system lets it.
    ///
    /// Caller cancellation keeps its meaning: Escape resolves the wait promptly
    /// with an <see cref="OperationCanceledException"/>, exactly as it did without the deadline.
    /// </summary>
    /// <remarks>
    /// Swift delivers cancellation through the ambient task; C# has no ambient task,
    /// so the operation receives the token it is expected to honour.
    /// </remarks>
    public static async Task<T> WithTranscriptionDeadline<T>(
        TimeSpan deadline,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // A caller cancelled before the race starts must not spawn an
        // uncancellable copy of the operation.
        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately not disposed: the abandoned operation keeps holding this token.
        var workCancellation = new CancellationTokenSource();
        using var watchdogCancellation = new CancellationTokenSource();
        var cancelledByCaller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelledByCaller.TrySetResult());

        var work = operation(workCancellation.Token);
        var watchdog = Task.Delay(deadline, watchdogCancellation.Token);

        var first = await Task.WhenAny(work, watchdog, cancelledByCaller.Task).ConfigureAwait(false);

        if (first == work)
        {
            watchdogCancellation.Cancel();
            return await work.ConfigureAwait(false);
        }

        workCancellation.Cancel();
        watchdogCancellation.Cancel();

        if (first == cancelledByCaller.Task)
        {
            // Woken by the caller's cancellation, not by the deadline.
            throw new OperationCanceledException(cancellationToken);
        }

        throw new TranscriptionTimeout(deadline);
    }
}

/// <summary>Recognition exceeded its deadline. The engine is presumed wedged.</summary>
public sealed class TranscriptionTimeout : Exception
{
    public TranscriptionTimeout(TimeSpan deadline)
        : base($"transcription exceeded its deadline of {deadline}")
    {
        Deadline = deadline;
    }

    public TimeSpan Deadline { get; }
}
