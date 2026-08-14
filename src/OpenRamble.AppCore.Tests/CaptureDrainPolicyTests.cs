using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// When the capture stops pulling samples, and how long a stop may wait on the machine.
///
/// These two are the whole of "the microphone stops when the key comes up", and both are here
/// rather than in the WASAPI file because both were broken there in ways no amount of reading
/// showed: the pull had no exit that did not depend on the audio stack telling the truth, and the
/// stop had no bound at all.
/// </summary>
public sealed class CaptureDrainPolicyTests
{
    /// <summary>Less than asked for means the device has given everything it had.</summary>
    [Fact]
    public void APartialReadEndsThePull() =>
        Assert.Equal(
            CaptureDrainStop.Exhausted,
            CaptureDrainPolicy.Next(900, CaptureDrainPolicy.ChunkSamples, chunksPulled: 1));

    /// <summary>Nothing at all ends it too, and says so differently.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnEmptyReadEndsThePull(int samplesRead) =>
        Assert.Equal(
            CaptureDrainStop.Empty,
            CaptureDrainPolicy.Next(samplesRead, CaptureDrainPolicy.ChunkSamples, chunksPulled: 1));

    /// <summary>A full buffer means there may be more behind it. Keep pulling.</summary>
    [Fact]
    public void AFullReadKeepsPulling() =>
        Assert.Equal(
            CaptureDrainStop.Continue,
            CaptureDrainPolicy.Next(
                CaptureDrainPolicy.ChunkSamples,
                CaptureDrainPolicy.ChunkSamples,
                chunksPulled: 1));

    /// <summary>
    /// <b>The regression.</b> A provider that answers every read in full for ever still lets go.
    /// </summary>
    /// <remarks>
    /// NAudio's <c>BufferedWaveProvider</c> defaults to <c>ReadFully = true</c>: a read of an
    /// empty buffer comes back as the full count of zeroes. The pull loop's only exit used to be
    /// "the provider gave me less than I asked for", so it never exited - it manufactured silence
    /// at processor speed, filled the frame queue with it, and the stop that had to drain that
    /// queue outlived the session's own deadline. What a person saw was "the microphone didn't
    /// stop when the key was released" and a dictation thrown away.
    ///
    /// <para>
    /// The capture now sets <c>ReadFully = false</c>, which is the fix. This is the guard behind
    /// it: an honest provider is no longer the only thing standing between a key release and a
    /// microphone that never closes.
    /// </para>
    /// </remarks>
    [Fact]
    public void APullThatIsAlwaysAnsweredInFullStillEnds()
    {
        int chunks = 0;
        CaptureDrainStop stop;
        do
        {
            chunks += 1;
            // The old defect, exactly: every read comes back complete, for ever.
            stop = CaptureDrainPolicy.Next(
                CaptureDrainPolicy.ChunkSamples,
                CaptureDrainPolicy.ChunkSamples,
                chunks);
            Assert.True(chunks <= 10_000, "the pull did not terminate");
        }
        while (stop == CaptureDrainStop.Continue);

        Assert.Equal(CaptureDrainStop.BudgetSpent, stop);
        Assert.Equal(CaptureDrainPolicy.MaximumChunksPerPull, chunks);
    }

    /// <summary>
    /// The budget is above anything a healthy device can hand over in one callback.
    /// </summary>
    /// <remarks>
    /// The capture buffer holds five seconds; a pull that has already produced more than that has
    /// been given audio the device could not have been holding. The guard has to sit above the
    /// real ceiling or it would cut a legitimate backlog short - which loses words, the thing it
    /// exists to prevent.
    /// </remarks>
    [Fact]
    public void TheBudgetIsAboveAnythingRealAudioCanFill()
    {
        double seconds =
            CaptureDrainPolicy.MaximumChunksPerPull * CaptureDrainPolicy.ChunkSamples / 16_000.0;

        Assert.True(seconds > 5, $"the budget stops a pull after {seconds:F1} s of real audio");
    }

    /// <summary>
    /// The stop reports back long before the session gives up on it.
    /// </summary>
    /// <remarks>
    /// The session drops a dictation whose capture did not close in twenty seconds. A stop whose
    /// own waits added up to more than that could never report anything but the failure it was
    /// built to avoid - with the recording already safely on disk.
    /// </remarks>
    [Fact]
    public void EveryWaitInsideAStopFitsWithinTheSessionsDeadline()
    {
        Assert.Equal(
            CaptureStopBudget.Drain + CaptureStopBudget.DeviceRelease,
            CaptureStopBudget.Total);
        Assert.True(CaptureStopBudget.FitsWithin(TimeSpan.FromSeconds(20)));
        Assert.False(CaptureStopBudget.FitsWithin(CaptureStopBudget.Total));
    }

    /// <summary>
    /// And the deadline it fits within is the one the session actually uses.
    /// </summary>
    /// <remarks>
    /// Read off the controller rather than written down twice: the previous number in this file
    /// would go on passing after somebody halved the real one.
    /// </remarks>
    [Fact]
    public void TheSessionsDeadlineIsTheOneTheBudgetsWereSizedAgainst() =>
        Assert.True(
            CaptureStopBudget.FitsWithin(DictationController.DefaultCaptureStopDeadline),
            $"the stop may wait {CaptureStopBudget.Total} against a deadline of "
            + $"{DictationController.DefaultCaptureStopDeadline}");
}
