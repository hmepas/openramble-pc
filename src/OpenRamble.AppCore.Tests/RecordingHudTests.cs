namespace OpenRamble.AppCore.Tests;

/// <summary>The recording pill: up whenever anything is being captured, and saying what.</summary>
public sealed class RecordingHudTests
{
    private static readonly TimeSpan Elapsed = TimeSpan.FromSeconds(754);

    [Fact]
    public void IdleHidesThePill()
    {
        RecordingHudContent content = RecordingHud.Content(MeetingRecordingState.Idle, Elapsed);

        Assert.False(content.IsVisible);
        Assert.Equal("", content.Title);
    }

    [Fact]
    public void RecordingShowsALitDotAndTheClock()
    {
        RecordingHudContent content = RecordingHud.Content(MeetingRecordingState.Recording, Elapsed);

        Assert.True(content.IsVisible);
        Assert.Equal("Recording", content.Title);
        Assert.Equal("12:34", content.Time);
        Assert.True(content.IsDotLit);
        Assert.Equal("Recording, 12 minutes 34 seconds", content.AccessibilityLabel);
    }

    [Fact]
    public void PausedKeepsTheClockAndDimsTheDot()
    {
        RecordingHudContent content = RecordingHud.Content(MeetingRecordingState.Paused, Elapsed);

        Assert.True(content.IsVisible);
        Assert.Equal("Paused", content.Title);
        Assert.Equal("12:34", content.Time);
        Assert.False(content.IsDotLit);
    }

    [Fact]
    public void StartingHasNoClockYetAndStoppingSaysItIsSaving()
    {
        RecordingHudContent starting = RecordingHud.Content(MeetingRecordingState.Starting, TimeSpan.Zero);
        RecordingHudContent stopping = RecordingHud.Content(MeetingRecordingState.Stopping, Elapsed);

        Assert.True(starting.IsVisible);
        Assert.Equal("", starting.Time);
        Assert.True(stopping.IsVisible);
        Assert.Equal("Saving recording…", stopping.Title);
        Assert.Equal("12:34", stopping.Time);
    }

    /// <summary>The right end of the dictation panel's edge, clear of the centred panel.</summary>
    [Fact]
    public void PillSitsAtTheRightEndOfThePanelsEdge()
    {
        var work = new OverlayFrame(0, 45, 3840, 2115);
        var pill = new OverlaySize(200, 40);

        Assert.Equal(new OverlayPoint(3616, 69), RecordingHud.Origin(DictationOverlayPlacement.Top, work, pill));
        Assert.Equal(new OverlayPoint(3616, 2096), RecordingHud.Origin(DictationOverlayPlacement.Bottom, work, pill));
    }
}
