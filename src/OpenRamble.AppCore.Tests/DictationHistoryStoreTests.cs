using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The history is the first thing in this product that keeps dictated text after quit. It has
/// to survive a relaunch, stay bounded, and take its audio with it when an entry goes.
/// </summary>
public sealed class DictationHistoryStoreTests : IDisposable
{
    private readonly string directory;
    private readonly DictationHistoryStore store;

    public DictationHistoryStoreTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid()}");
        store = new DictationHistoryStore(directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string MakeAudio(string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "fake wav");
        return path;
    }

    // MARK: - Persistence

    /// <summary>
    /// The point of the feature: it is still there after quit.
    ///
    /// This also catches a mismatch between how entries are written and how they are read - a
    /// writer and reader that disagree about dates lose the entire history at the next launch,
    /// silently and with no error.
    /// </summary>
    [Fact]
    public void TestEntriesSurviveAReload()
    {
        _ = store.Record("first take", null, 5);
        _ = store.Record("second take", null, 5);

        IReadOnlyList<HistoryEntry> reopened = new DictationHistoryStore(directory).Load();
        Assert.Equal(["second take", "first take"], reopened.Select(entry => entry.Text));
        Assert.NotEqual(default, reopened[0].Date);
    }

    [Fact]
    public void TestNewestEntryComesFirst()
    {
        _ = store.Record("older", null, 5);
        IReadOnlyList<HistoryEntry> entries = store.Record("newer", null, 5);
        Assert.Equal("newer", entries[0].Text);
    }

    /// <summary>
    /// Empty text is not a dictation. Recording it would fill the list with blank rows after
    /// every silent take.
    /// </summary>
    [Fact]
    public void TestBlankTextIsNotRecorded()
    {
        IReadOnlyList<HistoryEntry> entries = store.Record("   \n ", null, 5);
        Assert.Empty(entries);
    }

    // MARK: - Retention

    [Fact]
    public void TestTheListIsTrimmedToTheLimit()
    {
        for (int index = 0; index < 8; index++)
        {
            _ = store.Record($"take {index}", null, 5);
        }

        Assert.Equal(5, store.Load().Count);
        Assert.Equal("take 7", store.Load()[0].Text);
    }

    /// <summary>
    /// Audio is the expensive part. An entry that falls off the end must not leave its recording
    /// behind, or the folder grows without bound while the list looks correctly short.
    /// </summary>
    [Fact]
    public void TestEvictedEntriesTakeTheirAudioWithThem()
    {
        string first = MakeAudio("first-source.wav");
        _ = store.Record("oldest", first, 1);
        string storedName = Assert.IsType<string>(store.Load()[0].AudioFileName);
        string storedPath = Path.Combine(directory, storedName);
        Assert.True(File.Exists(storedPath));

        _ = store.Record("newest", null, 1);

        Assert.Equal(["newest"], store.Load().Select(entry => entry.Text));
        Assert.False(File.Exists(storedPath), "the evicted take's audio is still on disk");
    }

    /// <summary>
    /// Lowering the setting has to take effect on what is already stored, not only on what
    /// arrives next.
    /// </summary>
    [Fact]
    public void TestLoweringTheLimitTrimsWhatIsAlreadyThere()
    {
        for (int index = 0; index < 6; index++)
        {
            _ = store.Record($"take {index}", null, 10);
        }

        IReadOnlyList<HistoryEntry> trimmed = store.ApplyLimit(2);
        Assert.Equal(2, trimmed.Count);
        Assert.Equal(["take 5", "take 4"], store.Load().Select(entry => entry.Text));
    }

    // MARK: - Deleting

    [Fact]
    public void TestDeletingAnEntryRemovesItAndItsAudio()
    {
        string audio = MakeAudio("source.wav");
        _ = store.Record("keep", null, 5);
        _ = store.Record("remove", audio, 5);

        HistoryEntry target = store.Load().First(entry => entry.Text == "remove");
        string storedPath = Assert.IsType<string>(store.AudioPath(target));

        IReadOnlyList<HistoryEntry> remaining = store.Delete(target);
        Assert.Equal(["keep"], remaining.Select(entry => entry.Text));
        Assert.False(File.Exists(storedPath));
    }

    [Fact]
    public void TestDeletingEverythingLeavesNothingBehind()
    {
        string audio = MakeAudio("source.wav");
        _ = store.Record("one", audio, 5);
        _ = store.Record("two", null, 5);

        store.DeleteAll();
        Assert.Empty(store.Load());
        List<string> leftover = Directory.EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .Where(name => name!.EndsWith(".wav", StringComparison.Ordinal) && name != "source.wav")
            .Select(name => name!)
            .ToList();
        Assert.True(leftover.Count == 0, $"audio left behind: {string.Join(", ", leftover)}");
    }

    // MARK: - Audio that is not there

    /// <summary>
    /// A record whose file has gone is still worth showing: the text is the part people came for.
    /// </summary>
    [Fact]
    public void TestAnEntryWithoutAudioStillLoads()
    {
        _ = store.Record("text only", null, 5);
        HistoryEntry entry = store.Load()[0];
        Assert.Null(store.AudioPath(entry));
        Assert.Equal("text only", entry.Text);
    }

    [Fact]
    public void TestAMissingAudioFileIsReportedAsAbsent()
    {
        string audio = MakeAudio("source.wav");
        _ = store.Record("with audio", audio, 5);
        HistoryEntry entry = store.Load()[0];
        File.Delete(Assert.IsType<string>(store.AudioPath(entry)));
        Assert.Null(store.AudioPath(entry));
    }

    /// <summary>A take whose file cannot be copied still keeps its text.</summary>
    [Fact]
    public void TestAnUnreadableTakeStillRecordsTheText()
    {
        IReadOnlyList<HistoryEntry> entries = store.Record("words", Path.Combine(directory, "missing.wav"), 5);
        Assert.Equal("words", entries[0].Text);
        Assert.Null(entries[0].AudioFileName);
    }

    // MARK: - The setting

    [Fact]
    public void TestTheStoredLimitFallsBackToTheDefault()
    {
        Assert.Equal(5, DictationHistoryStore.ClampLimit(null));
        Assert.Equal(5, DictationHistoryStore.ClampLimit(0));
        Assert.Equal(20, DictationHistoryStore.ClampLimit(20));
        Assert.Equal(DictationHistoryStore.MaximumLimit, DictationHistoryStore.ClampLimit(10_000));
    }

    /// <summary>A truncated or hand-edited index must not take the app down.</summary>
    [Fact]
    public void TestACorruptIndexReadsAsEmpty()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "history.json"), "{ not json");
        Assert.Empty(store.Load());
    }
}

/// <summary>Starred dictations, and the promise they carry.</summary>
public sealed class KeptHistoryEntryTests : IDisposable
{
    private readonly string directory;

    public KeptHistoryEntryTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"history-kept-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private DictationHistoryStore Store() => new(directory);

    /// <summary>
    /// A star is a promise that the dictation stays. A promise a later dictation can break is
    /// not one, so a starred entry neither counts against the limit nor falls out of it.
    /// </summary>
    [Fact]
    public void TestStarredEntriesSurviveTheLimit()
    {
        DictationHistoryStore store = Store();
        // Written oldest-last, the way the store keeps them.
        for (int index = 0; index < 6; index++)
        {
            _ = store.Record(
                $"dictation {index}",
                null,
                100,
                DateTimeOffset.FromUnixTimeSeconds(1000 + index));
        }

        // Star the oldest, which is the first the limit would evict.
        HistoryEntry oldest = store.Load()[^1];
        _ = store.SetKept(true, oldest);

        IReadOnlyList<HistoryEntry> kept = store.ApplyLimit(2);

        Assert.True(
            kept.Any(entry => entry.Id == oldest.Id),
            "the starred one is the oldest and would have been evicted first");
        Assert.Equal(2, kept.Count(entry => !entry.IsKept));
    }

    /// <summary>
    /// Re-running recognition replaces the text and nothing else.
    ///
    /// The audio has to survive, or a second attempt would be impossible; the star has to
    /// survive, or re-running a kept dictation would quietly unprotect it.
    /// </summary>
    [Fact]
    public void TestReRecognisingKeepsTheAudioAndTheStar()
    {
        DictationHistoryStore store = Store();
        _ = store.Record("as first heard", null, 10);
        HistoryEntry entry = store.Load()[0];
        _ = store.SetKept(true, entry);

        IReadOnlyList<HistoryEntry> updated = store.ReplaceText("after the dictionary changed", entry);
        HistoryEntry changed = updated.First(candidate => candidate.Id == entry.Id);

        Assert.Equal("after the dictionary changed", changed.Text);
        Assert.Equal(entry.AudioFileName, changed.AudioFileName);
        Assert.True(changed.IsKept, "re-running must not unprotect a kept dictation");
        Assert.Equal(entry.Date, changed.Date);
    }

    /// <summary>
    /// A history written before stars existed must still load. A store that refused its own
    /// older files would lose the dictations this is for.
    /// </summary>
    [Fact]
    public void TestAHistoryWrittenBeforeStarsStillLoads()
    {
        string older =
            $$"""
            [{"id":"{{Guid.NewGuid()}}","date":"2026-08-01T10:00:00+00:00","text":"from an older build","audioFileName":null}]
            """;
        File.WriteAllText(Path.Combine(directory, "history.json"), older);

        IReadOnlyList<HistoryEntry> loaded = Store().Load();
        Assert.Single(loaded);
        Assert.Equal("from an older build", loaded[0].Text);
        Assert.False(loaded[0].IsKept);
    }

    /// <summary>
    /// The kind of app a take went to stays with it, so recognizing it again shapes the text
    /// the same way, and the new text is stored the way a take's text is.
    /// </summary>
    [Fact]
    public void TestTheAppKindStaysWithTheEntryAndReRecognitionIsTrimmedLikeATake()
    {
        DictationHistoryStore store = Store();
        _ = store.Record("Dear John.", null, 10, appCategory: ApplicationCategory.Mail);
        HistoryEntry entry = new DictationHistoryStore(directory).Load()[0];
        Assert.Equal(ApplicationCategory.Mail, entry.AppCategory);

        IReadOnlyList<HistoryEntry> updated = store.ReplaceText("Dear John,\n\nthanks. ", entry);
        HistoryEntry changed = updated.First(candidate => candidate.Id == entry.Id);
        Assert.Equal(ApplicationCategory.Mail, changed.AppCategory);
        Assert.Equal("Dear John,\n\nthanks.", changed.Text);
    }

    /// <summary>
    /// A history written before the app kind was kept, or by a later version that knows a kind
    /// this one does not, still loads; such an entry is recognized again verbatim, as it always
    /// was. The names are the ones the macOS history writes.
    /// </summary>
    [Fact]
    public void TestAHistoryWithoutAKnownAppKindStillLoads()
    {
        string older =
            $$"""
            [{"id":"{{Guid.NewGuid()}}","date":"2026-08-01T10:00:00+00:00","text":"from an older build","audioFileName":null},
             {"id":"{{Guid.NewGuid()}}","date":"2026-08-01T10:00:00+00:00","text":"from a newer build","audioFileName":null,"appCategory":"hologram"},
             {"id":"{{Guid.NewGuid()}}","date":"2026-08-01T10:00:00+00:00","text":"from a mac","audioFileName":null,"appCategory":"codeEditor"}]
            """;
        File.WriteAllText(Path.Combine(directory, "history.json"), older);

        IReadOnlyList<HistoryEntry> loaded = Store().Load();
        Assert.Equal(["from an older build", "from a newer build", "from a mac"], loaded.Select(entry => entry.Text));
        Assert.Equal([null, null, ApplicationCategory.CodeEditor], loaded.Select(entry => entry.AppCategory));
    }
}
