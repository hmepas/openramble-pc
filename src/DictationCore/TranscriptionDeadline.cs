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
    /// Twice real time, never below twenty seconds: generous enough that no
    /// healthy transcription ever meets it, proportional so an hour-long
    /// recording gets an hour-scale budget.
    /// </summary>
    public static TimeSpan Deadline(TimeSpan audioDuration) =>
        TimeSpan.FromSeconds(Math.Max(20, audioDuration.TotalSeconds * 2));

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
