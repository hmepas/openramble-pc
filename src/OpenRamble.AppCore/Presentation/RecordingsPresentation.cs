using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>Calendar sections belong to the library presentation, never to stored titles.</summary>
public sealed record RecordingDayGroup(DateOnly Day, IReadOnlyList<ILibraryRecording> Recordings)
{
    public static IReadOnlyList<RecordingDayGroup> Make(IEnumerable<ILibraryRecording> recordings, TimeZoneInfo? timeZone = null)
    {
        TimeZoneInfo zone = timeZone ?? TimeZoneInfo.Local;
        return recordings
            .GroupBy(recording => LibraryIndex.DayOf(recording.StartedAt, zone))
            .Select(group => new RecordingDayGroup(group.Key, group.OrderByDescending(recording => recording.StartedAt).ToList()))
            .OrderByDescending(group => group.Day)
            .ToList();
    }

    public bool NeedsSeconds(ILibraryRecording recording, TimeZoneInfo? timeZone = null) =>
        Recordings.Any(other => other.Id != recording.Id
            && LibraryIndex.SameMinute(other.StartedAt, recording.StartedAt, timeZone ?? TimeZoneInfo.Local));

    /// <summary>"Today", "Yesterday", else the date written out.</summary>
    public string Title(DateOnly today) => new LibraryDay(Day, []).Title(today);
}

/// <summary>Consecutive paragraphs from one audio source. Paragraph identity and timing stay intact.</summary>
public sealed class TranscriptSection
{
    private readonly List<MeetingUtterance> utterances;

    private TranscriptSection(MeetingUtterance first)
    {
        utterances = [first];
    }

    public IReadOnlyList<MeetingUtterance> Utterances => utterances;

    public Guid Id => utterances[0].Id;

    public MeetingChannel Channel => utterances[0].Channel;

    public static IReadOnlyList<TranscriptSection> Make(IEnumerable<MeetingUtterance> utterances)
    {
        var sections = new List<TranscriptSection>();
        foreach (MeetingUtterance utterance in utterances.OrderBy(u => u.Start))
        {
            if (sections.Count > 0 && sections[^1].Channel == utterance.Channel)
            {
                sections[^1].utterances.Add(utterance);
            }
            else
            {
                sections.Add(new TranscriptSection(utterance));
            }
        }
        return sections;
    }
}
