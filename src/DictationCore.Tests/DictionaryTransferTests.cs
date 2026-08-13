using System.Text;
using System.Text.Json;

namespace DictationCore.Tests;

/// <summary>
/// The dictionary travels between machines as a file: exact round-trips, honest
/// refusals for foreign files, and merges that never destroy.
/// </summary>
public sealed class DictionaryTransferTests
{
    private static DictionaryReplacement Replacement(
        string spoken,
        string written,
        bool inflects = true,
        bool noAcousticBoost = false,
        bool allowsPhoneticMatching = true) =>
        new(
            spoken: spoken,
            written: written,
            inflects: inflects,
            noAcousticBoost: noAcousticBoost,
            allowsPhoneticMatching: allowsPhoneticMatching);

    private static string[][] Contents(IReadOnlyList<DictionaryReplacement> replacements) =>
        replacements
            .Select(replacement => new[]
            {
                replacement.Spoken,
                replacement.Written,
                replacement.Inflects.ToString(),
                replacement.NoAcousticBoost.ToString(),
                replacement.AllowsPhoneticMatching.ToString(),
            })
            .ToArray();

    // MARK: - Round-trip

    [Fact]
    public void TestExportedFileImportsBackExactly()
    {
        DictionaryReplacement[] original =
        [
            Replacement("poust girls", "Postgres"),
            Replacement("центр", "Sentry", noAcousticBoost: true),
            Replacement("комет", "commit", inflects: false, allowsPhoneticMatching: false),
        ];

        IReadOnlyList<DictionaryReplacement> imported =
            DictionaryTransfer.Read(DictionaryTransfer.Export(original));

        Assert.Equal(Contents(original), Contents(imported));
    }

    /// <summary>
    /// The file carries no internal identifiers and no default-valued flags:
    /// it stays short, diffable and hand-editable.
    /// </summary>
    [Fact]
    public void TestExportedFileIsPortable()
    {
        byte[] data = DictionaryTransfer.Export([Replacement("a", "b")]);
        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("openRambleDictionary").GetInt32());
        JsonElement entries = root.GetProperty("replacements");
        Assert.Equal(1, entries.GetArrayLength());
        JsonElement entry = entries[0];
        // Internal ids must not leak into the file; default flags stay implicit.
        Assert.Equal(
            ["spoken", "written"],
            entry.EnumerateObject().Select(property => property.Name).Order());
    }

    /// <summary>A minimal hand-written file - just the pairs - imports with defaults.</summary>
    [Fact]
    public void TestHandWrittenFileWithBarePairsImports()
    {
        string file =
            """{"openRambleDictionary": 1, "replacements": [{"spoken": "Sentry", "written": "Sentry"}]}""";
        IReadOnlyList<DictionaryReplacement> imported =
            DictionaryTransfer.Read(Encoding.UTF8.GetBytes(file));

        Assert.Single(imported);
        Assert.True(imported[0].Inflects);
        Assert.False(imported[0].NoAcousticBoost);
        Assert.True(imported[0].AllowsPhoneticMatching);
    }

    // MARK: - Refusals

    [Fact]
    public void TestForeignJSONIsRefusedByName()
    {
        byte[] foreign = Encoding.UTF8.GetBytes("{\"someOtherApp\": true}");
        Assert.Throws<DictionaryTransfer.TransferError.NotADictionaryFile>(
            () => DictionaryTransfer.Read(foreign));
    }

    [Fact]
    public void TestGarbageIsReportedAsDamage()
    {
        Assert.Throws<DictionaryTransfer.TransferError.Damaged>(
            () => DictionaryTransfer.Read(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void TestNewerFormatIsRefusedPolitely()
    {
        byte[] future = Encoding.UTF8.GetBytes("{\"openRambleDictionary\": 2, \"replacements\": []}");
        var error = Assert.Throws<DictionaryTransfer.TransferError.NewerFormat>(
            () => DictionaryTransfer.Read(future));
        Assert.Equal(2, error.Version);
    }

    /// <summary>Whitespace-only entries carry no phrase - dropped, not imported.</summary>
    [Fact]
    public void TestEmptyEntriesAreDropped()
    {
        string file =
            """
            {"openRambleDictionary": 1, "replacements": [
                {"spoken": "  ", "written": "x"},
                {"spoken": "ok", "written": " trimmed  "}
            ]}
            """;
        IReadOnlyList<DictionaryReplacement> imported =
            DictionaryTransfer.Read(Encoding.UTF8.GetBytes(file));

        Assert.Single(imported);
        Assert.Equal("trimmed", imported[0].Written);
    }

    // MARK: - Merge

    [Fact]
    public void TestMergeAppendsNewAndUpdatesMatchesCaseInsensitively()
    {
        DictionaryReplacement[] existing =
        [
            Replacement("sentry", "Centry"),
            Replacement("deploy", "deploy"),
        ];
        DictionaryReplacement[] imported =
        [
            Replacement("Sentry", "Sentry"),
            Replacement("poust girls", "Postgres"),
        ];

        DictionaryTransfer.MergeResult merged =
            DictionaryTransfer.Merge(existing: existing, imported: imported);

        Assert.Equal(1, merged.Added);
        Assert.Equal(1, merged.Updated);
        // Updates edit in place, additions append in file order.
        Assert.Equal(
            ["Sentry", "deploy", "Postgres"],
            merged.Replacements.Select(replacement => replacement.Written));
        // An update keeps the row's identity.
        Assert.Equal(existing[0].Id, merged.Replacements[0].Id);
    }

    /// <summary>Importing a file that changes nothing reports exactly that.</summary>
    [Fact]
    public void TestMergeCountsOnlyRealChanges()
    {
        DictionaryReplacement[] existing = [Replacement("sentry", "Sentry")];
        DictionaryTransfer.MergeResult merged =
            DictionaryTransfer.Merge(existing: existing, imported: existing);

        Assert.Equal(0, merged.Added);
        Assert.Equal(0, merged.Updated);
        Assert.Equal(existing, merged.Replacements);
    }

    [Fact]
    public void TestMergeIntoEmptyDictionaryKeepsFileOrder()
    {
        DictionaryReplacement[] imported =
        [
            Replacement("b", "B"),
            Replacement("a", "A"),
        ];
        DictionaryTransfer.MergeResult merged =
            DictionaryTransfer.Merge(existing: [], imported: imported);

        Assert.Equal(2, merged.Added);
        Assert.Equal(["b", "a"], merged.Replacements.Select(replacement => replacement.Spoken));
    }
}
