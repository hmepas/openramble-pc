using System.Security.Cryptography;
using System.Text;

namespace LocalASR.Tests;

public class ModelVerifierTests : IDisposable
{
    private readonly string directory;

    public ModelVerifierTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"verifier-{Guid.NewGuid():D}");
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

        GC.SuppressFinalize(this);
    }

    private string Write(byte[] contents, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void AcceptsMatchingFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("привет, это тестовые данные");
        string path = Write(payload, "good.bin");
        var file = new ModelManifest.File("good.bin", payload.Length, Sha256(payload));

        new ModelVerifier().Verify(file, path);
    }

    [Fact]
    public void RejectsMissingFile()
    {
        var file = new ModelManifest.File("absent.bin", 10, new string('a', 64));
        string path = Path.Combine(directory, "absent.bin");

        var failure = Assert.Throws<ModelVerifier.Failure.FileMissing>(
            () => new ModelVerifier().Verify(file, path));
        Assert.Equal("absent.bin", failure.Path);
    }

    [Fact]
    public void RejectsSizeMismatchBeforeHashing()
    {
        byte[] payload = Enumerable.Repeat((byte)0x41, 100).ToArray();
        string path = Write(payload, "short.bin");
        // The declared size is larger than the real one - the size check must fire before the
        // sum is computed, because it is orders of magnitude cheaper.
        var file = new ModelManifest.File("short.bin", 500, Sha256(payload));

        var failure = Assert.Throws<ModelVerifier.Failure.SizeMismatch>(
            () => new ModelVerifier().Verify(file, path));
        Assert.Equal("short.bin", failure.Path);
        Assert.Equal(500, failure.Expected);
        Assert.Equal(100, failure.Actual);
    }

    [Fact]
    public void RejectsCorruptedContent()
    {
        // The size is the same, the content is not. Exactly the occasion the checksum exists
        // for: a broken download often preserves the length.
        byte[] payload = Enumerable.Repeat((byte)0x41, 128).ToArray();
        byte[] corrupted = Enumerable.Repeat((byte)0x42, 128).ToArray();
        string path = Write(corrupted, "corrupt.bin");
        var file = new ModelManifest.File("corrupt.bin", 128, Sha256(payload));

        var failure = Assert.Throws<ModelVerifier.Failure.ChecksumMismatch>(
            () => new ModelVerifier().Verify(file, path));
        Assert.Equal("corrupt.bin", failure.Path);
    }

    [Fact]
    public void HashesFileLargerThanOneChunk()
    {
        // The file is larger than the read chunk - check that the streaming count assembles
        // the sum of several pieces correctly.
        byte[] payload = new byte[9 * 1024 * 1024];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        string path = Write(payload, "big.bin");

        string digest = new ModelVerifier().Sha256(path, "big.bin");

        Assert.Equal(Sha256(payload), digest);
    }

    [Fact]
    public void EmptyFileHashesToKnownValue()
    {
        string path = Write([], "empty.bin");

        string digest = new ModelVerifier().Sha256(path, "empty.bin");

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", digest);
    }
}
