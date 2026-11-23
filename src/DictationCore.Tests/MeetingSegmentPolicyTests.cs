namespace DictationCore.Tests;

/// <summary><see cref="SpeechSegmenter"/> with a ceiling, producing absolute positions.</summary>
public sealed class MeetingSegmentPolicyTests
{
    private const int Frame = 1_600; // 100 ms
    private const float Speech = 0.5f;
    private const float Quiet = 0.0f;

    private static int Frames(double seconds) => (int)(seconds * 16_000);

    /// <summary>Feed <paramref name="seconds"/> of one level, collecting every segment produced.</summary>
    private static List<MeetingSegmentRef> Feed(MeetingSegmentPolicy policy, float level, double seconds)
    {
        List<MeetingSegmentRef> output = [];
        int remaining = Frames(seconds);
        while (remaining > 0)
        {
            int count = Math.Min(Frame, remaining);
            if (policy.Observe(level, count) is { } segment) { output.Add(segment); }
            remaining -= count;
        }
        return output;
    }

    [Fact]
    public void TestAPauseAfterTheMinimumSegmentCutsAtAbsoluteFrames()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.System, startFrame: 32_000);
        Assert.Empty(Feed(policy, Speech, seconds: 12));
        var cuts = Feed(policy, Quiet, seconds: 1);
        Assert.Single(cuts);
        var cut = cuts[0];
        Assert.Equal(MeetingChannel.System, cut.Channel);
        Assert.Equal(32_000, cut.StartFrame); // positions are absolute in the file
        // The cut lands inside the pause: after the speech, before its end.
        Assert.True(cut.FrameCount > Frames(12));
        Assert.True(cut.FrameCount < Frames(13));
    }

    [Fact]
    public void TestNothingIsCutBeforeTheMinimumSegment()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        Feed(policy, Speech, seconds: 5);
        Assert.Empty(Feed(policy, Quiet, seconds: 2));
        Assert.Equal(Frames(7), policy.PendingFrames);
    }

    [Fact]
    public void TestContinuousSpeechIsCutAtTheQuietestRecentFrameOnceItPassesTheCap()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        // 19.5 s loud, one softer frame, then loud again past the cap.
        Feed(policy, Speech, seconds: 19.5);
        Assert.Null(policy.Observe(0.1f, Frame));
        var cuts = Feed(policy, Speech, seconds: 1);
        Assert.Single(cuts);
        Assert.Equal(0, cuts[0].StartFrame);
        // At the end of the quiet frame, not at the cap.
        Assert.Equal(Frames(19.5) + Frame, cuts[0].FrameCount);
        // 19.5 s + the quiet frame + 1 s were fed; the cut took 19.6 s of it. The remainder is
        // accounted for exactly.
        Assert.Equal(Frames(20.6) - cuts[0].FrameCount, policy.PendingFrames);
    }

    [Fact]
    public void TestUniformlyLoudSpeechIsCutAtTheCap()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        var cuts = Feed(policy, Speech, seconds: 25);
        Assert.Single(cuts);
        Assert.Equal(Frames(20), cuts[0].FrameCount);
    }

    [Fact]
    public void TestAfterAForcedCutTheRemainderCanBeCutAgain()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        var all = Feed(policy, Speech, seconds: 20);
        all.AddRange(Feed(policy, Speech, seconds: 12));
        all.AddRange(Feed(policy, Quiet, seconds: 1));
        Assert.Equal(2, all.Count);
        Assert.Equal(all[0].EndFrame, all[1].StartFrame); // segments abut
    }

    [Fact]
    public void TestFlushReturnsTheTailOnlyIfSomeoneSpokeInIt()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        Feed(policy, Quiet, seconds: 3);
        Assert.Null(policy.Flush()); // three seconds of room tone is not a segment
        Feed(policy, Speech, seconds: 3);
        var tail = policy.Flush();
        Assert.Equal(Frames(3), tail?.StartFrame);
        Assert.Equal(Frames(3), tail?.FrameCount);
        Assert.Null(policy.Flush()); // flushed once
        Assert.Equal(0, policy.PendingFrames);
    }

    [Fact]
    public void TestTheRemainderOfAPauseCutIsNotFlushedAsSpeech()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        Feed(policy, Speech, seconds: 12);
        Assert.Single(Feed(policy, Quiet, seconds: 1));
        Assert.Null(policy.Flush()); // what is left after the cut is the second half of a pause
    }

    [Fact]
    public void TestASilentChannelProducesNoSegmentsAtAll()
    {
        // The property the cost model rests on: the other side's silence costs no decode.
        var policy = new MeetingSegmentPolicy(MeetingChannel.System);
        Assert.Empty(Feed(policy, Quiet, seconds: 120));
        Assert.Null(policy.Flush());
    }

    [Fact]
    public void TestAnHourOfSilenceBeforeTheFirstWordIsNotDecodedWithIt()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.System);
        Feed(policy, Quiet, seconds: 3_600);
        Assert.True(policy.PendingFrames <= Frames(1.1), "the start slides forward under silence");
        Feed(policy, Speech, seconds: 12);
        var cuts = Feed(policy, Quiet, seconds: 1);
        Assert.Single(cuts);
        Assert.True(cuts[0].StartFrame >= Frames(3_599), "the segment begins just before the word");
        Assert.True(cuts[0].FrameCount < Frames(14));
    }

    [Fact]
    public void TestLeadingSilenceDoesNotShiftAPauseCutIntoSpokenAudio()
    {
        var policy = new MeetingSegmentPolicy(MeetingChannel.Microphone);
        Feed(policy, Quiet, seconds: 5);
        Feed(policy, Speech, seconds: 12);
        var cuts = Feed(policy, Quiet, seconds: 1);
        Assert.Single(cuts);
        var cut = cuts[0];
        Assert.True(cut.StartFrame <= Frames(5));
        Assert.True(
            cut.EndFrame >= Frames(17),
            "a pause cut must include all twelve seconds of spoken audio after the leading silence");
        Assert.True(cut.EndFrame < Frames(18));
    }
    /// <summary>
    /// Port-local: upstream pins the per-channel floor through MeetingCapture
    /// (SystemMeetingDefaultTests); the parameters themselves are pinned here.
    /// </summary>
    [Fact]
    public void TestCaptureLowersTheSpeechFloorForTheSystemSideOnly()
    {
        var system = MeetingSegmentPolicy.Parameters.Capture(MeetingChannel.System);
        var microphone = MeetingSegmentPolicy.Parameters.Capture(MeetingChannel.Microphone);
        Assert.Equal(0.005f, system.Speech.SpeechPeak);
        Assert.Equal(0.02f, microphone.Speech.SpeechPeak);
        Assert.Equal(MeetingSegmentPolicy.Parameters.Meeting, microphone);
        Assert.Equal(TimeSpan.FromSeconds(10), system.Speech.MinimumSegment);
        Assert.Equal(TimeSpan.FromSeconds(20), system.HardCap);

        // A quiet other side at 0.006 is speech on the system channel and room tone on the mic.
        var quietOther = new MeetingSegmentPolicy(MeetingChannel.System, system);
        Feed(quietOther, 0.006f, seconds: 3);
        Assert.NotNull(quietOther.Flush());
        var quietMic = new MeetingSegmentPolicy(MeetingChannel.Microphone, microphone);
        Feed(quietMic, 0.006f, seconds: 3);
        Assert.Null(quietMic.Flush());
    }
}
