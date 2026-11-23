using DictationCore;
using Xunit;

namespace OpenRamble.AppCore.Tests;

public class RecordingsPresentationTests
{
    private static readonly TimeZoneInfo PlusThree = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    private static TestRecording Recording(string date) =>
        new(DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture), Kind: RecordingKind.Meeting);

    [Fact]
    public void LibraryGroupsByLocalDayAndKeepsNewestFirst()
    {
        var beforeMidnight = Recording("2026-09-10T20:59:00Z");
        var afterMidnight = Recording("2026-09-10T21:01:00Z");
        var later = Recording("2026-09-11T09:00:00Z");
        var groups = RecordingDayGroup.Make([beforeMidnight, later, afterMidnight], PlusThree);
        Assert.Equal(2, groups.Count);
        Assert.Equal([later.Id, afterMidnight.Id], groups[0].Recordings.Select(r => r.Id));
        Assert.Equal([beforeMidnight.Id], groups[^1].Recordings.Select(r => r.Id));
    }

    [Fact]
    public void SecondsDisambiguateOnlyMatchingMinutes()
    {
        var first = Recording("2026-09-10T12:31:05Z");
        var second = Recording("2026-09-10T12:31:49Z");
        var third = Recording("2026-09-10T12:32:00Z");
        var group = new RecordingDayGroup(new DateOnly(2026, 9, 10), [first, second, third]);
        Assert.True(group.NeedsSeconds(first, PlusThree));
        Assert.True(group.NeedsSeconds(second, PlusThree));
        Assert.False(group.NeedsSeconds(third, PlusThree));
    }

    [Fact]
    public void ChannelHeadingsGroupWithoutMergingOrLosingParagraphs()
    {
        var first = new MeetingUtterance(MeetingChannel.System, 0, 2, "First");
        var second = new MeetingUtterance(MeetingChannel.System, 2, 4, "Second");
        var you = new MeetingUtterance(MeetingChannel.Microphone, 4, 5, "Yes");
        var failed = new MeetingUtterance(MeetingChannel.System, 5, 6, "", isFailed: true);
        var sections = TranscriptSection.Make([failed, second, you, first]);
        Assert.Equal(3, sections.Count);
        Assert.Equal([first, second], sections[0].Utterances);
        Assert.Equal([first, second, you, failed], sections.SelectMany(s => s.Utterances));
        var revised = second with { Text = "Second, continued" };
        Assert.Equal(sections[0].Id, TranscriptSection.Make([first, revised])[0].Id);
        Assert.Empty(TranscriptSection.Make([]));
    }

    [Fact]
    public void FollowingYieldsToReadingAndResumesOnlyAtTheBottom()
    {
        var state = new TranscriptFollowState();
        Assert.True(state.FollowsLatest);
        state.UserScrolled(distanceFromBottom: 120);
        Assert.False(state.FollowsLatest);
        state.UserScrolled(distanceFromBottom: 25);
        Assert.False(state.FollowsLatest);
        state.UserScrolled(distanceFromBottom: 20);
        Assert.True(state.FollowsLatest);
        state.UserScrolled(distanceFromBottom: 200);
        state.ReturnToLatest();
        Assert.True(state.FollowsLatest);
    }
}
