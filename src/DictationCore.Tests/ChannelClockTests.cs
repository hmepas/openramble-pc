using Contiguous = DictationCore.ChannelClock.Placement.Contiguous;
using Resynchronized = DictationCore.ChannelClock.Placement.Resynchronized;

namespace DictationCore.Tests;

/// <summary>
/// Where a block goes: the sample count within a threshold, host time past it.
///
/// The property that matters is the last test — real drift over a real
/// meeting stays bounded. Everything before it is the mechanism that makes
/// that true.
/// </summary>
public sealed class ChannelClockTests
{
    [Fact]
    public void TheFirstBlockWithoutAClockStartsAtZeroAndTheRestFollowContiguously()
    {
        var clock = new ChannelClock();
        Assert.Equal(new Contiguous(0), clock.Place(null, 1024));
        Assert.Equal(new Contiguous(1024), clock.Place(null, 1024));
        Assert.Equal(new Contiguous(2048), clock.Place(null, 100));
    }

    [Fact]
    public void TheFirstBlockWithAClockStartsWhereTheClockSays()
    {
        var clock = new ChannelClock();
        Assert.Equal(new Contiguous(320), clock.Place(320, 1024));
        Assert.Equal(new Contiguous(1344), clock.Place(1344, 1024));
    }

    [Fact]
    public void JitterWithinTheThresholdIsIgnoredInFavourOfContiguity()
    {
        var clock = new ChannelClock(resyncThresholdFrames: 800);
        _ = clock.Place(0, 1024);
        // The host clock says 1300, the count says 1024: converter jitter,
        // and a seam here would be a 276-frame click for nothing.
        Assert.Equal(new Contiguous(1024), clock.Place(1300, 1024));
        Assert.Equal(new Contiguous(2048), clock.Place(1900, 1024));
    }

    [Fact]
    public void ALoneJumpPastTheThresholdIsNotDrift()
    {
        var clock = new ChannelClock(resyncThresholdFrames: 800);
        _ = clock.Place(0, 1024);
        // One late read is a stall, not a clock that wandered: the count wins.
        Assert.Equal(new Contiguous(1024), clock.Place(2000, 1024));
        Assert.Equal(new Contiguous(2048), clock.Place(2048, 1024));
    }

    [Fact]
    public void SkewSustainedForASecondResynchronizesByTheSmallestSkewSeen()
    {
        var clock = new ChannelClock(resyncThresholdFrames: 800);
        _ = clock.Place(0, 1_600);
        long expected = 1_600;
        // The device lost 1 000 frames: from here on the host clock is consistently ahead.
        long host = 1_600 + 1_000;
        ChannelClock.Placement placement;
        do
        {
            placement = clock.Place(host, 1_600);
            if (placement is Contiguous) { Assert.Equal(expected, placement.StartFrame); }
            expected = placement.StartFrame + 1_600;
            host += 1_600;
        }
        while (placement is Contiguous && host < 40_000);
        Assert.Equal(new Resynchronized(host - 1_600, 1_000), placement);
        Assert.True(host - 1_600 - 2_600 >= 16_000, "not before a second of evidence");
    }

    /// <summary>
    /// A reader that stalls for 300 ms and then catches up: the late reads are stamped late, the
    /// back-to-back ones after them barely advance the clock. Every sample still lands exactly
    /// after the one before it - nothing overwritten, no silence inserted.
    /// </summary>
    [Fact]
    public void AStallFollowedByABurstLosesNothingAndInsertsNoGap()
    {
        var clock = new ChannelClock(resyncThresholdFrames: 800);
        const int chunk = 1_600;
        long read = 0;       // frames the reader has taken; the device has made `now`
        long now = 0;
        long expected = 0;
        void Read(long at)
        {
            now = Math.Max(now, at);
            int count = (int)Math.Min(chunk, now - read);
            if (count <= 0) { return; }
            ChannelClock.Placement placement = clock.Place(now - count, count);
            Assert.Equal(new Contiguous(expected), placement);
            expected += count;
            read += count;
        }
        for (int i = 1; i <= 20; i++) { Read(i * 320); }
        // 300 ms without a read, then as fast as the pipe will go.
        Read(now + 4_800 + 320);
        while (read < now) { Read(now + 2); }
        for (int i = 0; i < 40; i++) { Read(now + 320); }
        Assert.Equal(now, read);
    }

    [Fact]
    public void ABlockBeforeTheStartIsClampedToZero()
    {
        var clock = new ChannelClock();
        Assert.Equal(new Contiguous(0), clock.Place(-500, 1024));
    }

    [Fact]
    public void ResetForgetsTheRunSoAPauseCanReanchor()
    {
        var clock = new ChannelClock();
        _ = clock.Place(0, 1024);
        clock.Reset();
        Assert.Equal(new Contiguous(5000), clock.Place(5000, 1024));
    }

    /// <summary>
    /// Ninety minutes on a device whose clock runs 100 ppm fast — an ordinary
    /// consumer figure — and then again at ten times that. The placed position
    /// never wanders more than one threshold from the host clock, and the
    /// number of seams stays a handful per hour, not a click per block.
    /// </summary>
    [Theory]
    [InlineData(100.0, 30)]
    [InlineData(1_000.0, 200)]
    [InlineData(-1_000.0, 200)]
    public void RealisticDriftOverAWholeMeetingStaysBoundedWithFewSeams(double ppm, int maximumSeams)
    {
        var clock = new ChannelClock(resyncThresholdFrames: 800);
        const int block = 1024;
        int blocks = 90 * 60 * 16_000 / block;
        int seams = 0;
        long maximumWander = 0;
        for (int i = 0; i < blocks; i++)
        {
            // The device delivers `block` samples per block but the host
            // clock advances slightly less (positive ppm: the device is fast)
            // or more (negative: slow).
            long host = (long)Math.Round((double)(i * block) * (1 - ppm / 1_000_000), MidpointRounding.AwayFromZero);
            ChannelClock.Placement placement = clock.Place(host, block);
            if (placement is Resynchronized)
            {
                seams++;
            }
            maximumWander = Math.Max(maximumWander, Math.Abs(placement.StartFrame - host));
        }
        Assert.True(maximumWander <= 800 + block, $"at {ppm} ppm");
        Assert.True(seams <= maximumSeams, $"at {ppm} ppm");
        Assert.True(seams > 0, $"at {ppm} ppm the drift has to be corrected at all");
    }
}
