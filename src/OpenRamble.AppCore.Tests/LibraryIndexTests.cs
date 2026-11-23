using Xunit;

namespace OpenRamble.AppCore.Tests;

/// <summary>A recording as the Library sees it, for tests.</summary>
internal sealed record TestRecording(
    DateTimeOffset StartedAt,
    string? Title = null,
    string TranscriptText = "",
    RecordingKind Kind = RecordingKind.VoiceNote) : ILibraryRecording
{
    public Guid Id { get; } = Guid.NewGuid();

    public TimeSpan Duration => TimeSpan.Zero;
}

/// <summary>
/// One library of everything said: dictations and recordings in one list by
/// day, with one search over both.
/// </summary>
public class LibraryIndexTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private static DateTimeOffset Date(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));

    private static HistoryEntry Dictation(string text, DateTimeOffset date) => new(date, text, null);

    private static TestRecording Recording(string? title, DateTimeOffset date, string transcript = "") =>
        new(date, title, transcript);

    private static IReadOnlyList<LibraryDay> Days(LibraryScope scope = LibraryScope.All, string query = "") =>
        LibraryIndex.Days(
            dictations:
            [
                Dictation("Send it to Ana by noon.", Date(7, 9, 38)),
                Dictation("Ship the menu bar fix before Friday.", Date(6, 16, 12)),
                Dictation("Напомни позвонить в банк в четыре.", Date(7, 9, 12)),
            ],
            recordings:
            [
                Recording("Weekly sync", Date(7, 8, 2), "We moved the café launch to Thursday."),
                Recording(null, Date(6, 17, 46), "Бюджет утвердили, ждём ёлку."),
            ],
            scope: scope,
            query: query,
            timeZone: Moscow);

    private static IEnumerable<DateTimeOffset> Found(string query) => Days(query: query).SelectMany(d => d.Items).Select(i => i.Date);

    /// <summary>Newest day first, newest item first, both kinds in one run.</summary>
    [Fact]
    public void DictationsAndRecordingsShareOneListByDay()
    {
        var all = Days();
        Assert.Equal([new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 6)], all.Select(d => d.Day));
        Assert.Equal([Date(7, 9, 38), Date(7, 9, 12), Date(7, 8, 2)], all[0].Items.Select(i => i.Date));
        Assert.Equal([Date(6, 17, 46), Date(6, 16, 12)], all[1].Items.Select(i => i.Date));
        Assert.IsType<LibraryItem.Recording>(all[0].Items[2]);
        Assert.IsType<LibraryItem.Dictation>(all[0].Items[0]);
    }

    [Fact]
    public void AScopeShowsOneKind()
    {
        Assert.Equal(3, Days(scope: LibraryScope.Dictations).SelectMany(d => d.Items).Count());
        Assert.Equal(2, Days(scope: LibraryScope.Recordings).SelectMany(d => d.Items).Count());
        Assert.All(Days(scope: LibraryScope.Recordings).SelectMany(d => d.Items), item => Assert.IsType<LibraryItem.Recording>(item));
    }

    /// <summary>
    /// One search: a dictation is found by its words, a recording by its
    /// name. Case does not matter, in either alphabet.
    /// </summary>
    [Fact]
    public void SearchFindsWordsAndNames()
    {
        Assert.Equal([Date(7, 9, 38)], Found("ana"));
        Assert.Equal([Date(7, 8, 2)], Found("WEEKLY"));
        Assert.Equal([Date(7, 9, 12)], Found("банк"));
        Assert.Single(Found("БАНК"));
        Assert.Empty(Days(query: "nothing like this"));
        // A blank query finds everything.
        Assert.Equal(5, Found("   ").Count());
    }

    /// <summary>
    /// Deliberate deviation from upstream (title only): a recording is also found by the words
    /// of its transcript - named or not, case and diacritics aside.
    /// </summary>
    [Fact]
    public void SearchFindsRecordingsByTheirTranscript()
    {
        Assert.Equal([Date(7, 8, 2)], Found("thursday"));
        Assert.Equal([Date(7, 8, 2)], Found("cafe launch"));
        // An untitled recording is found by its words.
        Assert.Equal([Date(6, 17, 46)], Found("БЮДЖЕТ"));
        Assert.Equal([Date(6, 17, 46)], Found("елку"));
        Assert.Empty(LibraryIndex.Days(
            dictations: [],
            recordings: [Recording("Weekly sync", Date(7, 8, 2), "Thursday")],
            scope: LibraryScope.Dictations,
            query: "thursday",
            timeZone: Moscow));
    }

    /// <summary>A day with nothing left after the search is not listed at all.</summary>
    [Fact]
    public void EmptyDaysAreNotListed()
    {
        Assert.Equal([new DateOnly(2026, 11, 6)], Days(query: "friday").Select(d => d.Day));
    }

    /// <summary>Seconds appear only where two recordings would otherwise read alike.</summary>
    [Fact]
    public void SecondsOnlyTellApartRecordingsOfOneMinute()
    {
        var first = Recording(null, Date(7, 10, 0, 5));
        var second = Recording(null, Date(7, 10, 0, 40));
        var alone = Recording(null, Date(7, 11, 0, 0));
        var day = LibraryIndex.Days(
            dictations: [Dictation("Same minute, but not a recording.", Date(7, 11, 0, 30))],
            recordings: [first, second, alone],
            timeZone: Moscow)[0];
        Assert.True(day.NeedsSeconds(first, Moscow));
        Assert.True(day.NeedsSeconds(second, Moscow));
        Assert.False(day.NeedsSeconds(alone, Moscow));
    }

    [Fact]
    public void TheDayTitleIsTodayYesterdayOrTheDate()
    {
        var today = new DateOnly(2026, 11, 7);
        Assert.Equal("Today", new LibraryDay(today, []).Title(today));
        Assert.Equal("Yesterday", new LibraryDay(today.AddDays(-1), []).Title(today));
        Assert.Equal("5 November 2026", new LibraryDay(today.AddDays(-2), []).Title(today));
    }
}
