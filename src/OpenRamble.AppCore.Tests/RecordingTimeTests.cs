using Xunit;

namespace OpenRamble.AppCore.Tests;

public class RecordingTimeTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ClockUnderAnHourIsMinutesAndSeconds()
    {
        Assert.Equal("0:00", RecordingTime.Clock(S(0)));
        Assert.Equal("0:07", RecordingTime.Clock(S(7.9)));
        Assert.Equal("1:05", RecordingTime.Clock(S(65)));
        Assert.Equal("48:12", RecordingTime.Clock(S(2_892)));
    }

    [Fact]
    public void ClockPastAnHourAddsTheHour()
    {
        Assert.Equal("1:00:00", RecordingTime.Clock(S(3_600)));
        Assert.Equal("1:23:45", RecordingTime.Clock(S(5_025)));
    }

    [Fact]
    public void NegativeReadsAsZero()
    {
        Assert.Equal("0:00", RecordingTime.Clock(S(-3)));
        Assert.Equal("0 seconds", RecordingTime.Spoken(S(-3)));
    }

    [Fact]
    public void SpokenFormAgreesWithTheClockAndUsesWords()
    {
        Assert.Equal("0 seconds", RecordingTime.Spoken(S(0)));
        Assert.Equal("1 second", RecordingTime.Spoken(S(1)));
        Assert.Equal("1 minute 5 seconds", RecordingTime.Spoken(S(65)));
        Assert.Equal("48 minutes 12 seconds", RecordingTime.Spoken(S(2_892)));
        Assert.Equal("1 hour", RecordingTime.Spoken(S(3_600)));
        Assert.Equal("1 hour 23 minutes 45 seconds", RecordingTime.Spoken(S(5_025)));
    }

    [Fact]
    public void BriefFormFloorsLikeTheClockSoTheTwoAgree()
    {
        Assert.Equal("6 s", RecordingTime.Brief(S(6.5)));
        Assert.Equal("0:06", RecordingTime.Clock(S(6.5)));
        Assert.Equal("42 s", RecordingTime.Brief(S(42)));
        Assert.Equal("1 min", RecordingTime.Brief(S(60)));
        Assert.Equal("48 min", RecordingTime.Brief(S(2_892)));
        Assert.Equal("1 h", RecordingTime.Brief(S(3_600)));
        Assert.Equal("1 h 23 min", RecordingTime.Brief(S(5_025)));
    }
}
