using DictationCore;
using Xunit;

namespace DictationCore.Tests;

public class MeetingTranscriptFormatterTests
{
    private readonly MeetingUtterance[] utterances =
    [
        new(MeetingChannel.System, 4.9, 6, "Yep, loud and clear."),
        new(MeetingChannel.Microphone, 0.5, 4.2, "Right, can everyone hear me?"),
        new(MeetingChannel.Microphone, 3_725, 3_730, string.Empty, isFailed: true),
    ];

    [Fact]
    public void PlainTextIsChronologicalWithSpeakerAndTimestamp() =>
        Assert.Equal(
            """
            You · 00:00:00
            Right, can everyone hear me?

            Others · 00:00:04
            Yep, loud and clear.

            You · 01:02:05
            [couldn't transcribe this part]
            """.ReplaceLineEndings("\n"),
            MeetingTranscriptFormatter.PlainText(utterances));

    /// <summary>Upstream's provenance line says "this Mac"; the port's says "this PC".</summary>
    [Fact]
    public void MarkdownCarriesTheTitleTheProvenanceAndAnyNote()
    {
        string markdown = MeetingTranscriptFormatter.Markdown(
            utterances.Take(2),
            title: "Weekly sync",
            subtitle: "3 September 2026 · 47 min · Meeting (You and Others)",
            note: "Only the microphone was recorded.");
        Assert.Equal(
            """
            # Weekly sync

            3 September 2026 · 47 min · Meeting (You and Others)

            > Recorded with OpenRamble. This transcript was produced on this PC.

            > Only the microphone was recorded.

            **You** · 00:00:00
            Right, can everyone hear me?

            **Others** · 00:00:04
            Yep, loud and clear.

            """.ReplaceLineEndings("\n"),
            markdown);
    }

    /// <summary>
    /// An imported file's sound is on the microphone's channel, and it is not "You": nobody knows
    /// who speaks in a file. It leaves the app with its times and no names.
    /// </summary>
    [Fact]
    public void AFileWhoseSpeakerIsUnknownIsWrittenWithoutNames()
    {
        MeetingUtterance[] lecture =
        [
            new(MeetingChannel.Microphone, 0.5, 4.2, "Good morning."),
            new(MeetingChannel.Microphone, 65, 70, "Let's begin."),
        ];
        Assert.Equal(
            """
            00:00:00
            Good morning.

            00:01:05
            Let's begin.
            """.ReplaceLineEndings("\n"),
            MeetingTranscriptFormatter.PlainText(lecture, speakers: false));
        string markdown = MeetingTranscriptFormatter.Markdown(lecture, title: "Lecture", subtitle: "Imported file", speakers: false);
        Assert.Contains("**00:01:05**\nLet's begin.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("You", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesCanBeRelabelledWithoutTouchingTheTranscript()
    {
        string text = MeetingTranscriptFormatter.PlainText(
            [utterances[0]],
            names: new Dictionary<MeetingChannel, string> { [MeetingChannel.System] = "Zoom call" });
        Assert.StartsWith("Zoom call · ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TimestampsAlwaysHaveThreeFields()
    {
        Assert.Equal("00:00:00", MeetingTranscriptFormatter.Timestamp(0));
        Assert.Equal("00:00:59", MeetingTranscriptFormatter.Timestamp(59.9));
        Assert.Equal("01:02:05", MeetingTranscriptFormatter.Timestamp(3_725));
    }
}
