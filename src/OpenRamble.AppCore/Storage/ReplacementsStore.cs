using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Storing the custom dictionary of replacements.
///
/// The dictionary is the only thing a person types into this application by hand, and the only
/// value that accumulates in it. So there is exactly one rule here:
/// <b>couldn't read it - don't write.</b>
///
/// Substituting an empty array for one that could not be read (as an earlier version did) means
/// overwriting the file with it at the very first edit. The dictionary disappears entirely,
/// silently and irreversibly, and it looks like "the settings reset themselves".
/// </summary>
/// <remarks>
/// The macOS original keeps the JSON in <c>UserDefaults</c>. Windows has no such store, so the
/// same bytes go to a file under <see cref="AppPaths"/>; every decision the original makes -
/// version probe first, quarantine as a copy, hands off anything from the future - is
/// unchanged, and so is the on-disk JSON shape.
/// </remarks>
public sealed class ReplacementsStore
{
    /// <summary>
    /// Storage format number.
    ///
    /// Needed for rollback. Open source is rolled back a version regularly, and the old build
    /// must see "this was written by something newer" and keep its hands off - otherwise
    /// rolling back for a day destroys a dictionary accumulated over a month.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Explicit, not inherited: the file shape has to stay byte-identical to what the macOS
    /// build writes, and that means <c>version</c> / <c>items</c> in camel case.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string path;
    private readonly Func<DateTimeOffset> now;

    public ReplacementsStore(string path, Func<DateTimeOffset>? now = null)
    {
        this.path = path;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Why the dictionary is locked for writing.</summary>
    public abstract record Problem
    {
        private Problem()
        {
        }

        public abstract string Message { get; }

        /// <summary>There is a file, but it could not be made sense of. The original bytes are set aside.</summary>
        public sealed record Unreadable(string QuarantinePath) : Problem
        {
            public override string Message =>
                "The replacement dictionary couldn't be read, so it can't be edited. "
                + "The previous data is preserved — nothing is lost.";
        }

        /// <summary>Written by a newer version of the application.</summary>
        public sealed record WrittenByNewerVersion(int Version) : Problem
        {
            public override string Message =>
                $"The replacement dictionary was written by a newer version of the app "
                + $"(format {Version}). It can't be edited so that version's data isn't lost.";
        }
    }

    /// <param name="Problem">Not <c>null</c> - the dictionary cannot be written.</param>
    public sealed record Load(IReadOnlyList<DictionaryReplacement> Replacements, Problem? Problem);

    /// <summary>Stored. The array is wrapped in an object just for the sake of the version number.</summary>
    private sealed record Envelope(int Version, IReadOnlyList<DictionaryReplacement> Items);

    /// <summary>
    /// Version number only, no content.
    ///
    /// Read separately and first. The version cannot be parsed together with the elements:
    /// a rollback catches exactly the case where the elements are written in an unfamiliar
    /// shape and parsing stumbles over them. "This is someone else's version, don't touch it"
    /// would then turn into "could not read" - and the new version's data would go under the
    /// knife.
    /// </summary>
    private sealed record VersionProbe(int Version);

    public Load Read()
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            // No file - a normal first launch, the dictionary is empty. Not an accident.
            return new Load([], null);
        }
        catch (DirectoryNotFoundException)
        {
            return new Load([], null);
        }

        VersionProbe? probe = TryDeserialize<VersionProbe>(data);
        if (probe is not null)
        {
            if (probe.Version < 1)
            {
                return new Load([], new Problem.Unreadable(Quarantine(data)));
            }

            if (probe.Version > CurrentVersion)
            {
                // Nothing is set aside and nothing is touched: the data is intact, just not
                // ours. A newer build will come back and read it itself.
                return new Load([], new Problem.WrittenByNewerVersion(probe.Version));
            }

            Envelope? envelope = TryDeserialize<Envelope>(data);
            if (envelope is null)
            {
                // Our version, but the contents do not parse. Reading half of it and writing
                // it back means losing the other half.
                return new Load([], new Problem.Unreadable(Quarantine(data)));
            }

            return new Load(envelope.Items, null);
        }

        // Old format: a bare array without a version number. Read as is; it moves to the new
        // shape on the first write.
        IReadOnlyList<DictionaryReplacement>? legacy = TryDeserialize<List<DictionaryReplacement>>(data);
        if (legacy is not null)
        {
            return new Load(legacy, null);
        }

        return new Load([], new Problem.Unreadable(Quarantine(data)));
    }

    public void Save(IReadOnlyList<DictionaryReplacement> replacements)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(
            new Envelope(CurrentVersion, replacements),
            SerializerOptions);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, data);
    }

    private static T? TryDeserialize<T>(byte[] data)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(data, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Put the unreadable bytes aside.
    ///
    /// Write to a NEW file, do not touch the original: the quarantine must be a copy, not a
    /// move, or the rescue operation itself loses the data.
    /// </summary>
    private string Quarantine(byte[] data)
    {
        string stamp = now().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string quarantinePath = $"{path}.unreadable.{stamp}";
        File.WriteAllBytes(quarantinePath, data);
        return quarantinePath;
    }
}
