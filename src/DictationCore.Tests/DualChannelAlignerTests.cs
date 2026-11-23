namespace DictationCore.Tests;

/// <summary>
/// One stereo timeline from two streams. Every test is about a property of the
/// output — monotonic, contiguous, silence where a channel had nothing — because
/// those are what the file format and the transcript rely on.
/// </summary>
public sealed class DualChannelAlignerTests
{
    private static float[] Constant(float value, int count) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void AVoiceNoteWritesSilenceOnTheSystemSideWithoutCountingAGap()
    {
        var aligner = new DualChannelAligner(new HashSet<MeetingChannel> { MeetingChannel.Microphone }, jitterFrames: 1_600);
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.5f, 3_200));

        DualChannelAligner.Emission emission = Assert.IsType<DualChannelAligner.Emission>(aligner.Drain());
        Assert.Equal(0, emission.StartFrame);
        // Emission lags the newest frame by the jitter window.
        Assert.Equal(1_600, emission.FrameCount);
        Assert.Equal(Constant(0.5f, 1_600), emission.Microphone);
        Assert.Equal(Constant(0, 1_600), emission.System);
        // An inactive channel is not missing.
        Assert.Equal(0, aligner.GapFrames.GetValueOrDefault(MeetingChannel.System));
        Assert.Equal(0, aligner.GapFrames.GetValueOrDefault(MeetingChannel.Microphone));
    }

    [Fact]
    public void AGapIsSilenceFilledOnThatChannelOnlyAndCounted()
    {
        var aligner = new DualChannelAligner(jitterFrames: 0);
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.5f, 3_200));
        aligner.Ingest(MeetingChannel.System, 0, Constant(-0.5f, 1_600));

        DualChannelAligner.Emission emission = Assert.IsType<DualChannelAligner.Emission>(aligner.Flush());
        Assert.Equal(Constant(0.5f, 3_200), emission.Microphone);
        Assert.Equal(Constant(-0.5f, 1_600), emission.System[0..1_600]);
        Assert.Equal(Constant(0, 1_600), emission.System[1_600..3_200]);
        Assert.Equal(1_600, aligner.GapFrames[MeetingChannel.System]);
        Assert.Equal(0, aligner.GapFrames.GetValueOrDefault(MeetingChannel.Microphone));
    }

    [Fact]
    public void AChannelThatStartsLateIsSilenceFilledBeforeItsFirstBlock()
    {
        var aligner = new DualChannelAligner(jitterFrames: 0);
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.5f, 2_000));
        aligner.Ingest(MeetingChannel.System, 1_000, Constant(-0.5f, 1_000));

        DualChannelAligner.Emission emission = Assert.IsType<DualChannelAligner.Emission>(aligner.Flush());
        Assert.Equal(Constant(0, 1_000), emission.System[0..1_000]);
        Assert.Equal(Constant(-0.5f, 1_000), emission.System[1_000..2_000]);
        Assert.Equal(1_000, aligner.GapFrames[MeetingChannel.System]);
    }

    [Fact]
    public void ALateBlockIsTruncatedAtTheCursorAndTheDroppedFramesAreCounted()
    {
        var aligner = new DualChannelAligner(jitterFrames: 0);
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.1f, 1_600));
        aligner.Ingest(MeetingChannel.System, 0, Constant(0.1f, 1_600));
        Assert.Equal(1_600, aligner.Flush()?.FrameCount);

        // 800 frames of this block are already in the past.
        aligner.Ingest(MeetingChannel.Microphone, 800, Constant(0.9f, 1_600));
        DualChannelAligner.Emission emission = Assert.IsType<DualChannelAligner.Emission>(aligner.Flush());
        // The past is never rewritten.
        Assert.Equal(1_600, emission.StartFrame);
        Assert.Equal(800, emission.FrameCount);
        Assert.Equal(Constant(0.9f, 800), emission.Microphone);
        Assert.Equal(800, aligner.DroppedLateFrames[MeetingChannel.Microphone]);
    }

    [Fact]
    public void ABlockEntirelyInThePastIsDroppedWhole()
    {
        var aligner = new DualChannelAligner(jitterFrames: 0);
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.1f, 3_200));
        _ = aligner.Flush();
        aligner.Ingest(MeetingChannel.Microphone, 500, Constant(0.9f, 1_000));
        Assert.Null(aligner.Flush());
        Assert.Equal(1_000, aligner.DroppedLateFrames[MeetingChannel.Microphone]);
    }

    [Fact]
    public void ADeadChannelNeverBlocksTheFile()
    {
        var aligner = new DualChannelAligner(jitterFrames: 1_600);
        int totalEmitted = 0;
        for (int block = 0; block < 10; block++)
        {
            aligner.Ingest(MeetingChannel.Microphone, block * 1_600, Constant(0.5f, 1_600));
            // The system side delivered twice and then went quiet for good.
            if (block < 2)
            {
                aligner.Ingest(MeetingChannel.System, block * 1_600, Constant(-0.5f, 1_600));
            }
            if (aligner.Drain() is { } emission)
            {
                totalEmitted += emission.FrameCount;
            }
        }
        // The file kept growing on the live channel alone.
        Assert.Equal(9 * 1_600, totalEmitted);
        Assert.Equal(7 * 1_600, aligner.GapFrames[MeetingChannel.System]);
        Assert.Equal(0, aligner.GapFrames.GetValueOrDefault(MeetingChannel.Microphone));
    }

    [Fact]
    public void EmissionsAreContiguousAndCoverEveryFrameExactlyOnce()
    {
        var aligner = new DualChannelAligner(jitterFrames: 700);
        long expectedNext = 0;
        long covered = 0;
        // Irregular block sizes on both sides, the system side slightly ahead.
        int[] microphoneBlocks = [1_024, 512, 2_048, 300, 1_024, 1_024];
        int[] systemBlocks = [1_000, 1_000, 1_000, 1_000, 1_000, 1_000];
        long microphoneStart = 0;
        long systemStart = 0;
        foreach ((int m, int s) in microphoneBlocks.Zip(systemBlocks))
        {
            aligner.Ingest(MeetingChannel.Microphone, microphoneStart, Constant(0.2f, m));
            microphoneStart += m;
            aligner.Ingest(MeetingChannel.System, systemStart, Constant(0.3f, s));
            systemStart += s;
            if (aligner.Drain() is { } emission)
            {
                // No gap and no overlap between emissions.
                Assert.Equal(expectedNext, emission.StartFrame);
                expectedNext = emission.StartFrame + emission.FrameCount;
                covered += emission.FrameCount;
            }
        }
        if (aligner.Flush() is { } tail)
        {
            Assert.Equal(expectedNext, tail.StartFrame);
            covered += tail.FrameCount;
        }
        Assert.Equal(Math.Max(microphoneStart, systemStart), covered);
        Assert.Equal(covered, aligner.EmittedFrames);
    }

    [Fact]
    public void SamplesLandAtTheirFrameNotAtTheEndOfTheQueue()
    {
        var aligner = new DualChannelAligner(jitterFrames: 0);
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.5f, 4_000));
        // A system block whose position says "frame 1000", whatever order it
        // arrived in.
        aligner.Ingest(MeetingChannel.System, 1_000, [0.7f, 0.8f, 0.9f]);
        DualChannelAligner.Emission emission = Assert.IsType<DualChannelAligner.Emission>(aligner.Flush());
        Assert.Equal(0f, emission.System[999]);
        Assert.Equal(0.7f, emission.System[1_000]);
        Assert.Equal(0.9f, emission.System[1_002]);
        Assert.Equal(0f, emission.System[1_003]);
    }

    [Fact]
    public void ABlockSpanningTheEmissionBoundaryIsSplitNotDuplicated()
    {
        var aligner = new DualChannelAligner(jitterFrames: 1_000);
        aligner.Ingest(MeetingChannel.Microphone, 0, Enumerable.Range(0, 3_000).Select(i => (float)i).ToArray());
        DualChannelAligner.Emission first = Assert.IsType<DualChannelAligner.Emission>(aligner.Drain());
        Assert.Equal(2_000, first.FrameCount);
        Assert.Equal(1_999f, first.Microphone[^1]);
        DualChannelAligner.Emission rest = Assert.IsType<DualChannelAligner.Emission>(aligner.Flush());
        Assert.Equal(2_000, rest.StartFrame);
        Assert.Equal(2_000f, rest.Microphone[0]);
        Assert.Equal(2_999f, rest.Microphone[^1]);
        Assert.Equal(0, aligner.GapFrames.GetValueOrDefault(MeetingChannel.Microphone));
    }

    [Fact]
    public void NothingToEmitReturnsNil()
    {
        var aligner = new DualChannelAligner();
        Assert.Null(aligner.Drain());
        Assert.Null(aligner.Flush());
        aligner.Ingest(MeetingChannel.Microphone, 0, Constant(0.5f, 100));
        // Inside the jitter window nothing is certain yet.
        Assert.Null(aligner.Drain());
        Assert.Equal(100, aligner.Flush()?.FrameCount);
    }
}
