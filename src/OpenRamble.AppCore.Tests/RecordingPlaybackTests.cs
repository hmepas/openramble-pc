using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

public sealed class RecordingPlaybackTests
{
    private static readonly MeetingUtterance First = new(MeetingChannel.Microphone, 0, 4, "one");
    private static readonly MeetingUtterance Second = new(MeetingChannel.Microphone, 4, 9, "two");
    private static readonly MeetingUtterance Third = new(MeetingChannel.Microphone, 12, 15, "three");

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(3.99, 0)]
    [InlineData(4.0, 1)] // the end belongs to the next paragraph
    [InlineData(14.5, 2)]
    public void TheParagraphHeardIsTheOneSpanningTheTime(double time, int expected)
    {
        MeetingUtterance[] utterances = [First, Second, Third];
        Assert.Equal(utterances[expected].Id, TranscriptPlayback.Current(utterances, time));
    }

    [Theory]
    [InlineData(10.0)] // between paragraphs
    [InlineData(15.0)] // past the last
    public void NoParagraphIsHeardOutsideAllOfThem(double time)
    {
        Assert.Null(TranscriptPlayback.Current([First, Second, Third], time));
    }

    [Fact]
    public void NothingIsHighlightedBeforeAnythingPlayed()
    {
        Assert.Null(TranscriptPlayback.Current([First], null));
    }

    [Fact]
    public void DraggingWhilePlayingPausesFollowsThePointerAndResumesWhereReleased()
    {
        var scrub = new TransportScrub();
        Assert.True(scrub.Begin(isPlaying: true, at: 5));
        scrub.Move(30);
        Assert.Equal(30, scrub.Displayed(playerPosition: 5));
        Assert.Equal((30.0, true), scrub.End());
        Assert.False(scrub.IsScrubbing);
        Assert.Equal(7, scrub.Displayed(playerPosition: 7));
    }

    [Fact]
    public void DraggingWhilePausedSeeksAndStaysPaused()
    {
        var scrub = new TransportScrub();
        Assert.False(scrub.Begin(isPlaying: false, at: 2));
        scrub.Move(8);
        Assert.Equal((8.0, false), scrub.End());
    }

    [Fact]
    public void ASecondBeginDuringADragKeepsTheFirstPlayingState()
    {
        var scrub = new TransportScrub();
        scrub.Begin(isPlaying: true, at: 1);
        Assert.False(scrub.Begin(isPlaying: false, at: 3));
        Assert.True(scrub.End()!.Value.Resume);
    }

    [Fact]
    public void MovesAndEndsOutsideADragDoNothing()
    {
        var scrub = new TransportScrub();
        scrub.Move(12);
        Assert.Null(scrub.End());
        Assert.Equal(4, scrub.Displayed(4));
    }

    [Fact]
    public void ACancelledDragResumesNothing()
    {
        var scrub = new TransportScrub();
        scrub.Begin(isPlaying: true, at: 1);
        scrub.Cancel();
        Assert.Null(scrub.End());
    }

    [Fact]
    public void WaveformColumnsTakeTheLoudestBucketOfEitherChannel()
    {
        float[] microphone = [0.1f, 0.5f, 0.2f, 0.0f];
        float[] system = [0.3f, 0.0f, 0.0f, 0.9f];
        Assert.Equal([0.5f, 0.9f], RecordingWaveformLayout.Columns([microphone, system], 2));
    }

    [Fact]
    public void FewerBucketsThanColumnsGivesOneColumnPerBucket()
    {
        Assert.Equal([0.2f, 0.4f], RecordingWaveformLayout.Columns([[0.2f, 0.4f]], 100));
    }

    [Fact]
    public void NoPeaksGiveNoColumns()
    {
        Assert.Empty(RecordingWaveformLayout.Columns([], 10));
        Assert.Empty(RecordingWaveformLayout.Columns([[]], 10));
    }
}
