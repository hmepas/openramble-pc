using DictationCore;
using Xunit;

namespace DictationCore.Tests;

/// <summary>Port addition: upstream has no test file for <c>MeetingDiskPolicy</c>.</summary>
public class MeetingDiskPolicyTests
{
    private const long MiB = 1_024 * 1_024;

    [Theory]
    [InlineData(0)]
    [InlineData(200 * MiB)]
    [InlineData(500 * MiB - 1)]
    public void BelowFiveHundredMegabytesARecordingCannotStart(long free) =>
        Assert.Equal(new MeetingDiskPolicy.Verdict.TooLowToStart(), MeetingDiskPolicy.VerdictToStart(free));

    [Fact]
    public void BelowTwoGigabytesItStartsWithTheMinutesLeft()
    {
        Assert.Equal(new MeetingDiskPolicy.Verdict.Low(136), MeetingDiskPolicy.VerdictToStart(500 * MiB));
        Assert.Equal(new MeetingDiskPolicy.Verdict.Low(559), MeetingDiskPolicy.VerdictToStart(2_048 * MiB - 1));
    }

    [Theory]
    [InlineData(2_048 * MiB)]
    [InlineData(100_000 * MiB)]
    public void FromTwoGigabytesItIsOk(long free) =>
        Assert.Equal(new MeetingDiskPolicy.Verdict.Ok(), MeetingDiskPolicy.VerdictToStart(free));

    [Fact]
    public void ARunningRecordingStopsBelowTwoHundredMegabytes()
    {
        Assert.True(MeetingDiskPolicy.MustStop(200 * MiB - 1));
        Assert.False(MeetingDiskPolicy.MustStop(200 * MiB));
    }

    [Fact]
    public void TheDiskIsCheckedEveryMinuteOfFrames() =>
        Assert.Equal(60 * 16_000, MeetingDiskPolicy.CheckEveryFrames);
}
