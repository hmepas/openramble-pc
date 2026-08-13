using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DictationCore;

/// <summary>
/// Moving the personal dictionary between machines as a file.
///
/// The file is a versioned JSON envelope, deliberately independent of the
/// on-disk store: no internal ids leak out, absent flags mean their defaults,
/// and a hand-edited file with just <c>spoken</c>/<c>written</c> pairs imports cleanly.
/// </summary>
public static class DictionaryTransfer
{
    /// <summary>
    /// Format marker and version. The marker keeps a random JSON file from
    /// silently importing as an empty dictionary; the version lets a future
    /// format refuse politely instead of misreading.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <remarks>
    /// The Swift original is an <c>Error</c> enum with associated values; here the cases
    /// are nested exception types so that <c>throw</c> stays honest.
    /// </remarks>
    public abstract class TransferError : Exception
    {
        private protected TransferError(string message)
            : base(message)
        {
        }

        public sealed class NotADictionaryFile : TransferError
        {
            public NotADictionaryFile()
                : base("This file is not an OpenRamble dictionary.")
            {
            }
        }

        public sealed class NewerFormat : TransferError
        {
            public NewerFormat(int version)
                : base($"This dictionary file needs a newer version of OpenRamble (format {version}).")
            {
                Version = version;
            }

            public int Version { get; }
        }

        public sealed class Damaged : TransferError
        {
            public Damaged(string detail)
                : base($"The dictionary file couldn't be read: {detail}")
            {
                Detail = detail;
            }

            public string Detail { get; }
        }
    }

    private sealed class Envelope
    {
        [JsonRequired]
        [JsonPropertyName("openRambleDictionary")]
        public int OpenRambleDictionary { get; set; }

        [JsonRequired]
        [JsonPropertyName("replacements")]
        public List<Entry> Replacements { get; set; } = [];
    }

    /// <summary>
    /// A portable entry: flags appear only when they differ from defaults,
    /// so exported files stay short and human-editable.
    /// </summary>
    /// <remarks>
    /// The properties are declared in alphabetical order because that is the order
    /// <c>System.Text.Json</c> writes them in, and the Swift encoder asks for
    /// <c>.sortedKeys</c>. An export must stay diffable against a macOS one.
    /// </remarks>
    private sealed class Entry
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("allowsPhoneticMatching")]
        public bool? AllowsPhoneticMatching { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("inflects")]
        public bool? Inflects { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("noAcousticBoost")]
        public bool? NoAcousticBoost { get; set; }

        [JsonRequired]
        [JsonPropertyName("spoken")]
        public string Spoken { get; set; } = "";

        [JsonRequired]
        [JsonPropertyName("written")]
        public string Written { get; set; } = "";

        internal static Entry Of(DictionaryReplacement replacement) =>
            new()
            {
                Spoken = replacement.Spoken,
                Written = replacement.Written,
                Inflects = replacement.Inflects ? null : false,
                NoAcousticBoost = replacement.NoAcousticBoost ? true : null,
                AllowsPhoneticMatching = replacement.AllowsPhoneticMatching ? null : false,
            };

        internal DictionaryReplacement? Replacement()
        {
            string spoken = Spoken.Trim();
            string written = Written.Trim();
            if (spoken.Length == 0 || written.Length == 0) { return null; }
            return new DictionaryReplacement(
                spoken: spoken,
                written: written,
                inflects: Inflects ?? true,
                noAcousticBoost: NoAcousticBoost ?? false,
                allowsPhoneticMatching: AllowsPhoneticMatching ?? true);
        }
    }

    /// <remarks>
    /// The port of Foundation's <c>[.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]</c>.
    /// The relaxed encoder is what keeps Cyrillic and slashes as themselves instead of
    /// <c>\uXXXX</c> - an exported file has to read the same on both platforms.
    /// </remarks>
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Export(IReadOnlyList<DictionaryReplacement> replacements) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new Envelope
            {
                OpenRambleDictionary = CurrentVersion,
                Replacements = replacements.Select(Entry.Of).ToList(),
            },
            ExportOptions);

    /// <summary>
    /// Parses a file into fresh replacements. Entries that are empty after
    /// trimming are dropped silently - they carry no phrase to import.
    /// </summary>
    public static IReadOnlyList<DictionaryReplacement> Read(byte[] data)
    {
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(data)
                ?? throw new JsonException("the file holds no dictionary");
        }
        catch (JsonException error)
        {
            // Valid JSON without our marker is someone else's file, not damage.
            if (IsJSONObject(data)) { throw new TransferError.NotADictionaryFile(); }
            throw new TransferError.Damaged(error.Message);
        }

        if (envelope.OpenRambleDictionary > CurrentVersion)
        {
            throw new TransferError.NewerFormat(envelope.OpenRambleDictionary);
        }

        return envelope.Replacements
            .Select(entry => entry.Replacement())
            .OfType<DictionaryReplacement>()
            .ToList();
    }

    /// <summary>
    /// The port of <c>JSONSerialization.jsonObject(with:)</c>: a top-level fragment
    /// is not an object there, so it is not one here either.
    /// </summary>
    private static bool IsJSONObject(byte[] data)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public sealed record MergeResult(
        IReadOnlyList<DictionaryReplacement> Replacements,
        int Added,
        int Updated);

    /// <summary>
    /// Imports never destroy: existing entries keep their place and identity,
    /// an imported entry with the same spoken phrase (case-insensitively)
    /// updates it, everything else is appended in file order. Entries that
    /// change nothing are not counted - "0 new, 0 updated" means the file and
    /// the dictionary already agree.
    /// </summary>
    public static MergeResult Merge(
        IReadOnlyList<DictionaryReplacement> existing,
        IReadOnlyList<DictionaryReplacement> imported)
    {
        var result = existing.ToList();
        var indexBySpoken = new Dictionary<string, int>();
        for (int index = 0; index < existing.Count; index += 1)
        {
            indexBySpoken[Key(existing[index].Spoken)] = index;
        }

        int added = 0;
        int updated = 0;
        foreach (DictionaryReplacement incoming in imported)
        {
            if (indexBySpoken.TryGetValue(Key(incoming.Spoken), out int index))
            {
                DictionaryReplacement current = result[index];
                // The row keeps its identity: an update is an edit, not a
                // delete-and-recreate that would reshuffle the list.
                var replaced = new DictionaryReplacement(
                    id: current.Id,
                    spoken: incoming.Spoken,
                    written: incoming.Written,
                    inflects: incoming.Inflects,
                    noAcousticBoost: incoming.NoAcousticBoost,
                    allowsPhoneticMatching: incoming.AllowsPhoneticMatching);
                if (replaced == current) { continue; }
                result[index] = replaced;
                updated += 1;
            }
            else
            {
                result.Add(incoming);
                indexBySpoken[Key(incoming.Spoken)] = result.Count - 1;
                added += 1;
            }
        }

        return new MergeResult(result, added, updated);
    }

    private static string Key(string spoken) => spoken.ToLowerInvariant();
}
