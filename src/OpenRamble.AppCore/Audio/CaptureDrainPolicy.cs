namespace OpenRamble.AppCore;

/// <summary>Why a pull from the capture pipeline stopped.</summary>
public enum CaptureDrainStop
{
    /// <summary>Keep pulling: the provider filled the buffer and may hold more.</summary>
    Continue,

    /// <summary>Nothing came back. The device has delivered everything it had.</summary>
    Empty,

    /// <summary>Less than asked for: the tail of what the device had, and the natural end.</summary>
    Exhausted,

    /// <summary>
    /// The provider kept answering in full past every reasonable amount. Stopped by the budget.
    /// </summary>
    BudgetSpent,
}

/// <summary>
/// When to stop pulling samples out of the capture pipeline.
///
/// The audio thread hands over bytes and the pull loop turns them into frames. The loop's exit
/// condition is the whole of this policy, and it is a policy rather than an <c>if</c> because
/// getting it wrong does not look like a bug: it looks like a microphone that never stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of a real defect.</b> NAudio's <c>BufferedWaveProvider</c> defaults to
/// <c>ReadFully = true</c>: an empty buffer answers a read with the full count of zeroes rather
/// than with nothing. A loop that pulls "until the provider gives less than asked" therefore
/// never ends - it manufactures silence at processor speed, floods the frame queue, and the
/// stop that has to drain that queue takes longer than the session's whole deadline. What the
/// person sees is "the microphone didn't stop when the key was released", and the cause is a
/// loop with no way out.
/// </para>
/// <para>
/// So there are two guards, and both are needed. <see cref="CaptureDrainStop.Empty"/> and
/// <see cref="CaptureDrainStop.Exhausted"/> are the honest ends, and they depend on the provider
/// telling the truth about how much audio it has. <see cref="CaptureDrainStop.BudgetSpent"/>
/// depends on nothing: however the audio stack misbehaves, the loop returns.
/// </para>
/// </remarks>
public static class CaptureDrainPolicy
{
    /// <summary>
    /// Samples per pull: a tenth of a second at the recording rate.
    /// </summary>
    /// <remarks>
    /// Small enough that the first frame arrives promptly, large enough that a dictation is not
    /// thousands of allocations.
    /// </remarks>
    public const int ChunkSamples = 1_600;

    /// <summary>
    /// How many full chunks one pull may take before the loop stops trusting the provider.
    /// </summary>
    /// <remarks>
    /// Sixty-four chunks is 6.4 seconds of audio out of a single callback. The capture buffer
    /// holds five seconds, so a healthy pull cannot come near this even after a long stall of
    /// the draining task; anything beyond it is a provider that is inventing audio.
    /// </remarks>
    public const int MaximumChunksPerPull = 64;

    /// <summary>
    /// Decide after one read whether to pull again.
    /// </summary>
    /// <param name="samplesRead">What the provider actually returned.</param>
    /// <param name="chunkSamples">What it was asked for.</param>
    /// <param name="chunksPulled">How many reads this pull has already made, including this one.</param>
    public static CaptureDrainStop Next(int samplesRead, int chunkSamples, int chunksPulled)
    {
        if (samplesRead <= 0) { return CaptureDrainStop.Empty; }
        if (samplesRead < chunkSamples) { return CaptureDrainStop.Exhausted; }

        return chunksPulled >= MaximumChunksPerPull
            ? CaptureDrainStop.BudgetSpent
            : CaptureDrainStop.Continue;
    }
}

/// <summary>
/// How long a stop may spend waiting on parts of the machine that may never answer.
///
/// The session gives the whole stop one deadline (<c>DictationController.captureStopDeadline</c>,
/// twenty seconds) and drops the dictation when it is breached. These are the budgets inside
/// that: each one bounds a wait that has no other bound, and their sum has to stay well under
/// the session's, or the capture would be abandoned by the session before it could report what
/// went wrong.
/// </summary>
public static class CaptureStopBudget
{
    /// <summary>
    /// Waiting for the frames already queued to reach the file.
    /// </summary>
    /// <remarks>
    /// Closing a WAV whose queue is drained is milliseconds of work; this is three seconds
    /// because the last word of the dictation is in that queue and is worth waiting for. Past
    /// it, the take is closed without its tail - which loses a word, where waiting loses the
    /// whole dictation.
    /// </remarks>
    public static TimeSpan Drain { get; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Waiting for the audio stack to let go of its capture thread.
    /// </summary>
    /// <remarks>
    /// NAudio's <c>Dispose</c> joins that thread with no timeout of its own, and the thread only
    /// exits when the driver returns from a read. On a virtual or half-dead audio path it may
    /// not. The recording is already closed by then, so the only thing lost by giving up is the
    /// device handle - held by a background thread that the process outlives.
    /// </remarks>
    public static TimeSpan DeviceRelease { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest a stop can take before it gives up on everything it waits for.</summary>
    public static TimeSpan Total => Drain + DeviceRelease;

    /// <summary>
    /// Does the stop still report inside the session's deadline?
    /// </summary>
    /// <remarks>
    /// The margin is not decoration. A stop that finished at the same moment the session gave up
    /// would be reported as a wedged capture - the failure it was avoiding - and the dictation
    /// would be dropped with its audio already safely on disk.
    /// </remarks>
    public static bool FitsWithin(TimeSpan sessionDeadline) => Total * 2 <= sessionDeadline;
}
