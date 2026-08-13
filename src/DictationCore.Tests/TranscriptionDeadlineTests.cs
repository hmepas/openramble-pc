using System.Diagnostics;
using static DictationCore.TranscriptionDeadline;

namespace DictationCore.Tests;

/// <summary>
/// The deadline that turns a wedged engine into a bounded, recoverable
/// failure instead of an eternal "Transcribing…".
/// </summary>
public sealed class TranscriptionDeadlineTests
{
    /// <summary>
    /// Suspend forever and never resume - even under cancellation.
    ///
    /// The closest honest model of an inference call stuck on a dead system
    /// service: deaf to cancellation, holding no thread. The tempting
    /// alternative - a loop around a cancellable delay - models the same wedge
    /// but turns into a hot spin the moment the watchdog cancels it.
    /// </summary>
    private static Task<T> SuspendForever<T>() =>
        new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    // MARK: - Policy

    /// <summary>
    /// Short dictations get the twenty-second floor; long ones scale at
    /// twice real time, so no healthy transcription ever meets the deadline.
    /// </summary>
    [Fact]
    public void TestDeadlineScalesWithAudioButNeverDropsBelowFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), Deadline(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(20), Deadline(TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(20), Deadline(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(60), Deadline(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(3600), Deadline(TimeSpan.FromSeconds(1800)));
    }

    // MARK: - Race

    [Fact]
    public async Task TestFastOperationWinsUntouched()
    {
        string value = await WithTranscriptionDeadline(
            TimeSpan.FromSeconds(5),
            _ => Task.FromResult("done"));
        Assert.Equal("done", value);
    }

    [Fact]
    public async Task TestOperationErrorPropagatesAsItself()
    {
        await Assert.ThrowsAsync<Boom>(
            () => WithTranscriptionDeadline<int>(
                TimeSpan.FromSeconds(5),
                _ => throw new Boom()));
    }

    /// <summary>
    /// The premise of the whole mechanism: an operation that ignores
    /// cancellation and never returns must not hold the caller hostage.
    /// </summary>
    [Fact]
    public async Task TestWedgedOperationTimesOutPromptly()
    {
        long started = Stopwatch.GetTimestamp();
        // Deliberately shrugs off cancellation, like a stuck inference call.
        await Assert.ThrowsAsync<TranscriptionTimeout>(
            () => WithTranscriptionDeadline(
                TimeSpan.FromMilliseconds(80),
                _ => SuspendForever<int>()));
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            "the caller must be free at the deadline, not at the operation's mercy");
    }

    /// <summary>
    /// Escape keeps its meaning: caller cancellation resolves the wait as
    /// cancellation, not as a timeout.
    /// </summary>
    [Fact]
    public async Task TestCallerCancellationResolvesAsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var task = Task.Run(
            () => WithTranscriptionDeadline(
                TimeSpan.FromSeconds(30),
                _ => SuspendForever<int>(),
                cancellation.Token),
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    /// <summary>
    /// A race entered by an already-cancelled caller must not spawn an
    /// uncancellable copy of the operation.
    /// </summary>
    [Fact]
    public async Task TestAlreadyCancelledCallerNeverStartsTheOperation()
    {
        bool ran = false;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var task = Task.Run(
            () => WithTranscriptionDeadline(
                TimeSpan.FromSeconds(30),
                _ =>
                {
                    ran = true;
                    return Task.FromResult(1);
                },
                cancellation.Token),
            CancellationToken.None);
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(ran);
    }

    private sealed class Boom : Exception;
}
