using System.Globalization;

namespace OpenRamble.AppCore;

/// <summary>
/// What the Library shows: everything said, in one list.
///
/// Dictations and recordings were two windows with two lists, and a person
/// looking for something they said had to know which kind it was first. Here
/// they are one list by day, newest first, with one search over both.
/// </summary>
public enum LibraryScope
{
    All,
    Dictations,
    Recordings,
}

/// <summary>What kind of thing a recording is; <see cref="RecordingsPlaceholder.Kind"/> says it in words.</summary>
public enum RecordingKind
{
    VoiceNote,
    Meeting,
    ImportedFile,
    ScreenRecording,
}

/// <summary>
/// A recording as the Library lists and searches it.
///
/// The seam to the stored recording (DictationCore <c>MeetingRecordingMetadata</c> plus its
/// transcript): the Library needs only these, so it does not depend on how a recording is kept.
/// </summary>
public interface ILibraryRecording
{
    Guid Id { get; }

    DateTimeOffset StartedAt { get; }

    /// <summary>The name the person gave it, or <c>null</c>.</summary>
    string? Title { get; }

    TimeSpan Duration { get; }

    RecordingKind Kind { get; }

    /// <summary>The transcript's words so far, joined; empty when there are none.</summary>
    string TranscriptText { get; }
}

/// <summary>One thing that was said: a dictation, or a recording.</summary>
public abstract record LibraryItem
{
    private LibraryItem()
    {
    }

    public sealed record Dictation(HistoryEntry Entry) : LibraryItem;

    public sealed record Recording(ILibraryRecording Value) : LibraryItem;

    public Guid Id => this switch
    {
        Dictation d => d.Entry.Id,
        Recording r => r.Value.Id,
        _ => throw new InvalidOperationException(),
    };

    public DateTimeOffset Date => this switch
    {
        Dictation d => d.Entry.Date,
        Recording r => r.Value.StartedAt,
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// Whether the search finds it: a dictation by its words, a recording by its name or by the
    /// words of its transcript (a deliberate deviation: upstream searches recordings by name
    /// only). Case and diacritics do not matter; an empty query finds everything.
    /// </summary>
    public bool Matches(string query)
    {
        string needle = query.Trim();
        if (needle.Length == 0) { return true; }
        return this switch
        {
            Dictation d => Contains(d.Entry.Text, needle),
            Recording r => Contains(r.Value.Title ?? "", needle) || Contains(r.Value.TranscriptText, needle),
            _ => false,
        };
    }

    private static bool Contains(string haystack, string needle) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

/// <summary>One day of the Library, newest item first.</summary>
public sealed record LibraryDay(DateOnly Day, IReadOnlyList<LibraryItem> Items)
{
    /// <summary>"Today", "Yesterday", else the date written out.</summary>
    public string Title(DateOnly today)
    {
        if (Day == today) { return "Today"; }
        if (Day == today.AddDays(-1)) { return "Yesterday"; }
        return Day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Two recordings started in the same minute need seconds to be told
    /// apart; nothing else does.
    /// </summary>
    public bool NeedsSeconds(ILibraryRecording recording, TimeZoneInfo? timeZone = null) =>
        Items.Any(item => item is LibraryItem.Recording other
            && other.Value.Id != recording.Id
            && LibraryIndex.SameMinute(other.Value.StartedAt, recording.StartedAt, timeZone ?? TimeZoneInfo.Local));
}

public static class LibraryIndex
{
    public static IReadOnlyList<LibraryDay> Days(
        IEnumerable<HistoryEntry> dictations,
        IEnumerable<ILibraryRecording> recordings,
        LibraryScope scope = LibraryScope.All,
        string query = "",
        TimeZoneInfo? timeZone = null)
    {
        TimeZoneInfo zone = timeZone ?? TimeZoneInfo.Local;
        var items = new List<LibraryItem>();
        if (scope != LibraryScope.Recordings) { items.AddRange(dictations.Select(entry => new LibraryItem.Dictation(entry))); }
        if (scope != LibraryScope.Dictations) { items.AddRange(recordings.Select(recording => new LibraryItem.Recording(recording))); }
        return items
            .Where(item => item.Matches(query))
            .GroupBy(item => DayOf(item.Date, zone))
            .Select(group => new LibraryDay(group.Key, group.OrderByDescending(item => item.Date).ToList()))
            .OrderByDescending(day => day.Day)
            .ToList();
    }

    /// <summary>The local calendar day a moment falls on.</summary>
    internal static DateOnly DayOf(DateTimeOffset date, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(date, timeZone).DateTime);

    internal static bool SameMinute(DateTimeOffset a, DateTimeOffset b, TimeZoneInfo timeZone)
    {
        DateTime x = TimeZoneInfo.ConvertTime(a, timeZone).DateTime;
        DateTime y = TimeZoneInfo.ConvertTime(b, timeZone).DateTime;
        return x.Date == y.Date && x.Hour == y.Hour && x.Minute == y.Minute;
    }
}
