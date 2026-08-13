using System.Text;
using System.Text.Json;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Storing the dictionary of replacements.
///
/// The dictionary is the only thing a person types into this application by hand. Every test
/// here is about one thing: unreadable must not turn into empty and overwrite what accumulated.
/// </summary>
public sealed class ReplacementsStoreTests : IDisposable
{
    private readonly string directory;
    private readonly string path;
    private readonly ReplacementsStore store;

    public ReplacementsStoreTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"openramble-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "replacements.json");
        store = new ReplacementsStore(
            path,
            () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private void Write(string json) => File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));

    private string? StoredJson() => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

    // MARK: - Ordinary life

    [Fact]
    public void Scenario001()
    {
        ReplacementsStore.Load loaded = store.Read();

        Assert.Empty(loaded.Replacements);
        Assert.Null(loaded.Problem);
    }

    [Fact]
    public void Scenario002()
    {
        DictionaryReplacement[] items =
        [
            new("Sentry", "Sentry"),
            new("pull request", "pull request"),
        ];

        store.Save(items);

        Assert.Equal(items, store.Read().Replacements);
        Assert.Null(store.Read().Problem);
    }

    [Fact]
    public void Scenario003()
    {
        store.Save([new DictionaryReplacement("code", "code")]);

        using JsonDocument document = JsonDocument.Parse(Assert.IsType<string>(StoredJson()));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("items", out _));
    }

    [Fact]
    public void Scenario004()
    {
        store.Save([new DictionaryReplacement("code", "code")]);
        store.Save([]);

        Assert.Empty(store.Read().Replacements);
        Assert.Null(store.Read().Problem);
    }

    // MARK: - Moving from the old format

    [Fact]
    public void Scenario005()
    {
        DictionaryReplacement[] items = [new("deploy", "deploy")];
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(items));

        ReplacementsStore.Load loaded = store.Read();

        Assert.Equal(items, loaded.Replacements);
        Assert.Null(loaded.Problem);
    }

    [Fact]
    public void Scenario006()
    {
        DictionaryReplacement[] items = [new("deploy", "deploy")];
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(items));

        store.Save(store.Read().Replacements);

        using JsonDocument document = JsonDocument.Parse(Assert.IsType<string>(StoredJson()));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(items, store.Read().Replacements);
    }

    // MARK: - Unreadable

    /// <summary>
    /// Broken JSON.
    ///
    /// An earlier version returned an empty array here - and the first edit of the dictionary
    /// wrote it over the file. The person lost everything they had accumulated, silently and for
    /// good.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        Write("{this is not json");

        ReplacementsStore.Load loaded = store.Read();

        var problem = Assert.IsType<ReplacementsStore.Problem.Unreadable>(loaded.Problem);
        Assert.Empty(loaded.Replacements);
        // The original data is still there - the quarantine copied it, it did not move it.
        Assert.Equal("{this is not json", StoredJson());
        Assert.Equal("{this is not json", File.ReadAllText(problem.QuarantinePath, Encoding.UTF8));
    }

    [Fact]
    public void Scenario008()
    {
        Write("""{"version": 1, "items": [{"id": "not""");

        Assert.IsType<ReplacementsStore.Problem.Unreadable>(store.Read().Problem);
    }

    [Fact]
    public void Scenario009()
    {
        Write("""{"favouriteColour": "blue"}""");

        Assert.IsType<ReplacementsStore.Problem.Unreadable>(store.Read().Problem);
    }

    [Fact]
    public void Scenario010()
    {
        // Empty data is not "no dictionary" but "something went wrong": a missing dictionary
        // looks like a missing file.
        File.WriteAllBytes(path, []);

        Assert.IsType<ReplacementsStore.Problem.Unreadable>(store.Read().Problem);
    }

    [Fact]
    public void Scenario011()
    {
        Write("""{"version": 0, "items": []}""");

        Assert.IsType<ReplacementsStore.Problem.Unreadable>(store.Read().Problem);
    }

    [Fact]
    public void Scenario012()
    {
        // Parsing half of it and writing it back means losing the other half.
        Write("""{"version": 1, "items": [{"spoken": "code"}]}""");

        Assert.IsType<ReplacementsStore.Problem.Unreadable>(store.Read().Problem);
    }

    // MARK: - A version from the future

    /// <summary>
    /// Rolling the application back one version.
    ///
    /// The old build must see somebody else's format and keep its hands off: otherwise a rollback
    /// for a day destroys a dictionary accumulated over a month.
    /// </summary>
    [Fact]
    public void Scenario013()
    {
        const string future = """{"version": 99, "items": [{"id": "x", "spoken": "a", "written": "b"}]}""";
        Write(future);

        ReplacementsStore.Load loaded = store.Read();

        Assert.Equal(new ReplacementsStore.Problem.WrittenByNewerVersion(99), loaded.Problem);
        Assert.Empty(loaded.Replacements);
        Assert.Equal(future, StoredJson());
        // No quarantine is needed here: the data is intact, just not ours.
        Assert.Empty(Directory.GetFiles(directory, "*.unreadable.*"));
    }

    [Fact]
    public void Scenario014()
    {
        const string future = """{"version": 99, "items": []}""";
        Write(future);

        // The store itself forbids nothing - the ban is held by the app state. All that matters
        // here is that reading did not replace the data with an empty one.
        store.Read();
        Assert.Equal(future, StoredJson());
    }
}
