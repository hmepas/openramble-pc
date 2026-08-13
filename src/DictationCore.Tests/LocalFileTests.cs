using System.Text;

namespace DictationCore.Tests;

/// <summary>
/// File reading exists as a separate type for the sake of one promise: the application
/// does not go online. Reading by arbitrary URL accepts any address and silently via http
/// goes outside, which is why the network surface check would not be able to distinguish
/// reading settings from an undeclared send. Refusal for a non-file address -
/// not a trifle, but what this check rests on.
/// </summary>
public sealed class LocalFileTests : IDisposable
{
    private readonly string directory;

    public LocalFileTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"localfile-{Guid.NewGuid()}");
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
    }

    [Fact]
    public void TestRefusesAnyAddressThatIsNotAFile()
    {
        // Exactly the case for which the type was written: an address leading outside,
        // rejected before any reading.
        foreach (string address in new[]
        {
            "https://huggingface.co/model.json",
            "http://localhost:8080/settings",
            "ftp://example.org/file",
        })
        {
            var url = new Uri(address);

            var failure = Assert.Throws<LocalFile.Failure.NotAFileURL>(() => LocalFile.Read(url));
            Assert.Equal(url.Scheme, failure.Scheme);
        }
    }

    [Fact]
    public void TestReadsLocalFileByteForByte()
    {
        string path = Path.Combine(directory, "манифест.json");
        byte[] payload = Encoding.UTF8.GetBytes("{\"модель\": \"parakeet\"}");
        File.WriteAllBytes(path, payload);

        Assert.Equal(payload, LocalFile.Read(new Uri(path)));
    }

    [Fact]
    public void TestMissingFileIsAnError()
    {
        // A missing manifest is a broken application build, and we cannot be silent
        // about this: without a manifest, the model installation has no root of trust.
        string path = Path.Combine(directory, "нет-такого.json");

        Assert.Throws<LocalFile.Failure.Unreadable>(() => LocalFile.Read(new Uri(path)));
    }

    [Fact]
    public void TestEmptyFileReadsAsEmptyData()
    {
        // An empty file is not a read error. The one who will deal with the emptiness is the one
        // who requested the content.
        string path = Path.Combine(directory, "пусто.json");
        File.WriteAllBytes(path, []);

        Assert.Equal([], LocalFile.Read(new Uri(path)));
    }
}
