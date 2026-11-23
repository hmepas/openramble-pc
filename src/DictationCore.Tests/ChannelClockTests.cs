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
    public void DriftPastTheThresholdResynchronizesAndReportsTheSkew()
    {
        var clock = new ChannelClock(resyncThresholdFrames: 800);
        _ = clock.Place(0, 1024);
        Assert.Equal(new Resynchronized(2000, 976), clock.Place(2000, 1024));
        // And the run continues from the new anchor.
        Assert.Equal(new Contiguous(3024), clock.Place(3024, 1024));
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
            // clock advances slightly less: the device is fast.
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
