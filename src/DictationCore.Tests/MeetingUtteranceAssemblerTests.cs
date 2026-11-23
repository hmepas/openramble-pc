using DictationCore;
using Xunit;

namespace DictationCore.Tests;

public class MeetingUtteranceAssemblerTests
{
    private static MeetingUtteranceAssembler.Segment Segment(
        MeetingChannel channel, double start, double end, string text, bool failed = false) =>
        new(channel, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, failed);

    private static MeetingUtteranceAssembler.Parameters Params(double gap = 1.2, double soft = 12, double hard = 40) => new()
    {
        Gap = TimeSpan.FromSeconds(gap),
        SoftDuration = TimeSpan.FromSeconds(soft),
        HardDuration = TimeSpan.FromSeconds(hard),
    };

    [Fact]
    public void SameChannelSegmentsWithinTheGapMergeIntoOneParagraph()
    {
        var assembler = new MeetingUtteranceAssembler();
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 0, 4, "Right, can everyone")));
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 4.5, 6, "hear me?")));
        var closed = assembler.Flush();
        Assert.Single(closed);
        Assert.Equal("Right, can everyone hear me?", closed[0].Text);
        Assert.Equal(TimeSpan.Zero, closed[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), closed[0].End);
        Assert.Equal(MeetingChannel.Microphone, closed[0].Channel);
    }

    [Fact]
    public void TheOtherSideSpeakingClosesTheParagraph()
    {
        var assembler = new MeetingUtteranceAssembler();
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 0, 4, "Can everyone hear me?"));
        var closed = assembler.Append(Segment(MeetingChannel.System, 4.2, 6, "Yes, loud and clear."));
        Assert.Equal(["Can everyone hear me?"], closed.Select(u => u.Text));
        Assert.Equal([MeetingChannel.System], assembler.Flush().Select(u => u.Channel));
    }

    [Fact]
    public void APauseLongerThanTheGapClosesTheParagraph()
    {
        var assembler = new MeetingUtteranceAssembler(Params(gap: 1.2));
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 0, 4, "One thought."));
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 5.1, 7, "Another")));
        Assert.Equal(
            ["One thought. Another"],
            assembler.Append(Segment(MeetingChannel.Microphone, 8.5, 9, "later thought")).Select(u => u.Text));
    }

    [Fact]
    public void ASentenceEndClosesOnlyOncePastTheSoftLength()
    {
        var assembler = new MeetingUtteranceAssembler(Params(gap: 5, soft: 12, hard: 40));
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 0, 5, "Short so far."));
        // A full stop at five seconds is not a paragraph.
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 5, 10, "Still merging.")));
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 10, 13, "Now past twelve."));
        Assert.Equal(
            ["Short so far. Still merging. Now past twelve."],
            assembler.Append(Segment(MeetingChannel.Microphone, 13, 15, "New one")).Select(u => u.Text));
    }

    [Fact]
    public void TheHardLengthClosesWhateverTheText()
    {
        var assembler = new MeetingUtteranceAssembler(Params(gap: 5, soft: 12, hard: 40));
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 0, 20, "no punctuation here"));
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 20, 41, "and none here either"));
        Assert.Single(assembler.Append(Segment(MeetingChannel.Microphone, 41, 45, "next")));
    }

    [Fact]
    public void AFailedSegmentIsItsOwnClosedParagraphAndMergesWithNothing()
    {
        var assembler = new MeetingUtteranceAssembler();
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 0, 4, "Before."));
        var closed = assembler.Append(Segment(MeetingChannel.Microphone, 4, 8, "", failed: true));
        Assert.Equal(2, closed.Count);
        Assert.Equal("Before.", closed[0].Text);
        Assert.True(closed[1].IsFailed);
        Assert.Equal(TimeSpan.FromSeconds(4), closed[1].Start);
        // The next one opens fresh.
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 8, 10, "After.")));
        Assert.Equal(["After."], assembler.Flush().Select(u => u.Text));
    }

    [Fact]
    public void SilenceDecodedAsNothingNeitherOpensNorCloses()
    {
        var assembler = new MeetingUtteranceAssembler();
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 0, 3, "   ")));
        _ = assembler.Append(Segment(MeetingChannel.Microphone, 3, 5, "Hello"));
        Assert.Empty(assembler.Append(Segment(MeetingChannel.Microphone, 5, 6, "")));
        Assert.Equal(["Hello"], assembler.Flush().Select(u => u.Text));
    }

    [Fact]
    public void SentenceEndingsAllowAClosingQuote()
    {
        Assert.True(MeetingUtteranceAssembler.EndsSentence("Done."));
        Assert.True(MeetingUtteranceAssembler.EndsSentence("Really?\""));
        Assert.True(MeetingUtteranceAssembler.EndsSentence("Wait…"));
        Assert.False(MeetingUtteranceAssembler.EndsSentence("and then"));
        Assert.False(MeetingUtteranceAssembler.EndsSentence(""));
    }
}
