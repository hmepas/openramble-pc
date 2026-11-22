using System.Text.Json;
using System.Text.Json.Serialization;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>One finished dictation, kept so it can be heard and reused.</summary>
/// <param name="AudioFileName">
/// The take's audio, if it is still on disk. A file can go missing - the person may have
/// deleted it, or a crash may have left the record without it - and an entry whose text is
/// intact is still worth showing.
/// </param>
/// <param name="IsKept">
/// Kept regardless of the retention limit.
///
/// Decoded with a default rather than required, so histories written by earlier versions still
/// load. A store that refused its own older files would lose the very dictations this flag
/// exists to protect.
/// </param>
/// <param name="AppCategory">
/// The kind of app the take went to, so recognizing it again shapes the text the same way:
/// paragraphs where it went to prose, "Send it." left off where it went to a terminal.
///
/// Absent on entries written before it was kept, and on a kind a later version added; those
/// are recognized again verbatim, as they always were, rather than failing the whole history.
/// The kind, never the app's name.
/// </param>
[method: JsonConstructor]
public sealed record HistoryEntry(
    Guid Id,
    DateTimeOffset Date,
    string Text,
    string? AudioFileName,
    bool IsKept = false,
    [property: JsonConverter(typeof(LenientApplicationCategoryConverter))] ApplicationCategory? AppCategory = null)
{
    public HistoryEntry(
        DateTimeOffset date,
        string text,
        string? audioFileName,
        bool isKept = false,
        ApplicationCategory? appCategory = null)
        : this(Guid.NewGuid(), date, text, audioFileName, isKept, appCategory)
    {
    }
}

/// <summary>
/// <see cref="ApplicationCategory"/> under the names the macOS history writes ("terminal",
/// "codeEditor"), and nothing - rather than a failed history - for a name this build does not
/// know.
/// </summary>
internal sealed class LenientApplicationCategoryConverter : JsonConverter<ApplicationCategory?>
{
    public override ApplicationCategory? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        string? name = reader.GetString();
        foreach (ApplicationCategory category in Enum.GetValues<ApplicationCategory>())
        {
            if (Name(category) == name) { return category; }
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, ApplicationCategory? value, JsonSerializerOptions options)
    {
        if (value is { } category)
        {
            writer.WriteStringValue(Name(category));
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static string Name(ApplicationCategory category) =>
        JsonNamingPolicy.CamelCase.ConvertName(category.ToString());
}

/// <summary>
/// The last few dictations, on disk, with their audio.
///
/// This replaces a recovery mechanism that only surfaced takes which had <i>failed</i>: tickets,
/// deletion-intent receipts, file leases and session tombstones, whose entire purpose was to
/// hand back words the app could not insert. A visible history does that job and more - every
/// take can be replayed, copied or re-inserted, not only the broken ones - and it does it with
/// a list and a retention count.
///
/// <b>This is a deliberate change to what the product persists.</b> Until now recognized text
/// lived in memory and was gone at quit. Keeping it, with the audio, is what makes the feature
/// useful, and it is why the privacy section of the README has to be rewritten rather than
/// quietly left alone. Nothing leaves the machine; the trade is that something now stays on it.
/// </summary>
/// <remarks>
/// The index is written beside itself and moved over, like <see cref="SettingsStore"/>: a
/// process killed mid-write must not turn the whole history into a file that does not parse.
/// </remarks>
public sealed class DictationHistoryStore
{
    /// <summary>How many takes to keep by default.</summary>
    public const int DefaultLimit = 5;

    /// <summary>The settings key. The same spelling as the macOS defaults key.</summary>
    public const string SettingsKey = "historyLimit";

    /// <summary>
    /// The widest the setting goes. A limit is not a promise to keep nothing - it is a promise
    /// that the number is finite and the person chose it.
    /// </summary>
    public const int MaximumLimit = 50;

    private const string IndexFileName = "history.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string directory;

    public DictationHistoryStore(string directory)
    {
        this.directory = directory;
    }

    /// <summary>
    /// The stored limit, clamped. An absent or nonsensical value reads as the default rather
    /// than as "keep everything".
    /// </summary>
    public static int ClampLimit(int? stored)
    {
        if (stored is not { } value) { return DefaultLimit; }
        if (value <= 0) { return DefaultLimit; }
        return Math.Min(value, MaximumLimit);
    }

    private string IndexPath => Path.Combine(directory, IndexFileName);

    /// <summary>The audio file's path, or <c>null</c> when the entry has none or it is gone.</summary>
    public string? AudioPath(HistoryEntry entry)
    {
        if (entry.AudioFileName is not { } name) { return null; }
        string path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    public IReadOnlyList<HistoryEntry> Load()
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(IndexPath);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        // A corrupt index is not worth failing the app over, and it is not worth silently
        // deleting either: returning nothing leaves the file in place for anyone who wants to
        // look at it.
        try
        {
            return JsonSerializer.Deserialize<List<HistoryEntry>>(data, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Record a take, trim to <paramref name="limit"/>, and delete the audio of what fell off.
    ///
    /// Returns the list as it now stands. Audio for evicted entries is removed here rather than
    /// by a sweep: the moment an entry leaves the list is the only moment its file is provably
    /// unreferenced.
    ///
    /// Empty text is a take the model found no words in. It is kept for its audio, to be played
    /// and recognized again, so without the audio there is nothing to keep, and an audio copy
    /// that fails is thrown rather than quietly kept as nothing.
    /// </summary>
    /// <exception cref="IOException">A wordless take's audio could not be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">A wordless take's audio could not be copied.</exception>
    public IReadOnlyList<HistoryEntry> Record(
        string text,
        string? audio,
        int limit,
        DateTimeOffset? date = null,
        ApplicationCategory? appCategory = null)
    {
        string trimmedText = text.Trim();
        if (trimmedText.Length == 0 && audio is null) { return Load(); }

        Directory.CreateDirectory(directory);

        string? storedName = null;
        if (audio is not null)
        {
            string name = $"{Guid.NewGuid()}.wav";
            string destination = Path.Combine(directory, name);
            try
            {
                File.Copy(audio, destination, overwrite: false);
                storedName = name;
            }
            catch (Exception error) when (trimmedText.Length > 0
                && error is IOException or UnauthorizedAccessException)
            {
                // A missing or unreadable take must not cost the person the text.
            }
        }

        var entries = new List<HistoryEntry>(Load());
        entries.Insert(0, new HistoryEntry(date ?? DateTimeOffset.Now, trimmedText, storedName, appCategory: appCategory));
        List<HistoryEntry> kept = Trimmed(entries, limit);
        Write(kept);
        return kept;
    }

    /// <summary>Trim an already-stored history to a new limit, deleting evicted audio.</summary>
    public IReadOnlyList<HistoryEntry> ApplyLimit(int limit)
    {
        IReadOnlyList<HistoryEntry> entries = Load();
        List<HistoryEntry> kept = Trimmed(entries, limit);
        if (kept.Count == entries.Count) { return entries; }
        Write(kept);
        return kept;
    }

    /// <summary>
    /// <paramref name="entries"/> within <paramref name="limit"/>, with the audio of every
    /// evicted entry deleted.
    /// </summary>
    /// <remarks>
    /// The limit counts ordinary dictations. A starred one is a promise that it stays, and a
    /// promise that the next dictation can break is not one, so starred entries neither count
    /// against the limit nor fall out of it. A take with no words is kept to be heard again,
    /// beside the dictations rather than instead of one: takes with no words count against the
    /// same limit among themselves, so they never push a dictation out. Order is untouched, so
    /// the list still reads by date.
    /// </remarks>
    private List<HistoryEntry> Trimmed(IReadOnlyList<HistoryEntry> entries, int limit)
    {
        int dictations = Math.Max(1, limit);
        int wordless = Math.Max(1, limit);
        var kept = new List<HistoryEntry>(entries.Count);
        foreach (HistoryEntry entry in entries)
        {
            if (entry.IsKept)
            {
                kept.Add(entry);
                continue;
            }

            if (entry.Text.Length == 0)
            {
                if (wordless <= 0) { continue; }
                wordless--;
            }
            else
            {
                if (dictations <= 0) { continue; }
                dictations--;
            }

            kept.Add(entry);
        }

        var keptIds = kept.Select(entry => entry.Id).ToHashSet();
        foreach (HistoryEntry evicted in entries)
        {
            if (!keptIds.Contains(evicted.Id)) { RemoveAudio(evicted); }
        }

        return kept;
    }

    /// <summary>
    /// Put new text on an entry, leaving its audio, star and app kind alone.
    ///
    /// Trimmed exactly as <see cref="Record"/> trims, so recognizing a take again stores what
    /// the take itself would have stored.
    /// </summary>
    public IReadOnlyList<HistoryEntry> ReplaceText(string text, HistoryEntry entry)
    {
        var entries = new List<HistoryEntry>(Load());
        int index = entries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index < 0) { return entries; }
        entries[index] = entries[index] with { Text = text.Trim() };
        Write(entries);
        return entries;
    }

    /// <summary>Star or unstar one entry.</summary>
    public IReadOnlyList<HistoryEntry> SetKept(bool isKept, HistoryEntry entry)
    {
        var entries = new List<HistoryEntry>(Load());
        int index = entries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index < 0) { return entries; }
        entries[index] = entries[index] with { IsKept = isKept };
        Write(entries);
        return entries;
    }

    public IReadOnlyList<HistoryEntry> Delete(HistoryEntry entry)
    {
        List<HistoryEntry> remaining = Load().Where(candidate => candidate.Id != entry.Id).ToList();
        RemoveAudio(entry);
        Write(remaining);
        return remaining;
    }

    /// <summary>Remove every entry and its audio.</summary>
    public void DeleteAll()
    {
        foreach (HistoryEntry entry in Load()) { RemoveAudio(entry); }
        try
        {
            File.Delete(IndexPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RemoveAudio(HistoryEntry entry)
    {
        if (entry.AudioFileName is not { } name) { return; }
        try
        {
            File.Delete(Path.Combine(directory, name));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Write(IReadOnlyList<HistoryEntry> entries)
    {
        Directory.CreateDirectory(directory);
        string staging = IndexPath + ".new";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(entries, SerializerOptions));
        File.Move(staging, IndexPath, overwrite: true);
    }
}
