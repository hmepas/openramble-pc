using System.Buffers.Binary;
using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

public sealed class WAVWriterTests : IDisposable
{
    private readonly string directory;

    public WAVWriterTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"wav-{Guid.NewGuid()}");
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

    private WAVWriter MakeWriter(string name = "take.wav") =>
        new(Path.Combine(directory, name), sampleRate: 16_000, channels: 1);

    [Fact]
    public void TestProducesFileReadableByTheSystem()
    {
        var writer = MakeWriter();
        writer.Open();
        // Half a second of a sine wave.
        float[] samples = [.. Enumerable.Range(0, 8000).Select(index => MathF.Sin(index * 0.05f) * 0.5f)];
        writer.Append(samples);
        string path = writer.Close();

        // Main check: the file must be readable by standard means,
        // otherwise recognition won't read it.
        var file = WavFile.Read(path);
        Assert.Equal(16_000, file.SampleRate);
        Assert.Equal(1, file.Channels);
        Assert.Equal(8000, file.Length);
    }

    [Fact]
    public void TestHeaderSizesAreFixedUpOnClose()
    {
        var writer = MakeWriter();
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.1f, 1600)]);
        string path = writer.Close();

        byte[] data = File.ReadAllBytes(path);
        // 44 bytes of header plus 1600 samples of two bytes.
        Assert.Equal(44 + 3200, data.Length);

        // The header must contain real sizes, not the blank zeros.
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40, 4));
        Assert.Equal(36 + 3200, (int)riffSize);
        Assert.Equal(3200, (int)dataSize);
    }

    [Fact]
    public void TestDurationTracksWhatWasWritten()
    {
        var writer = MakeWriter();
        writer.Open();

        Assert.Equal(TimeSpan.Zero, writer.Duration);
        writer.Append([.. Enumerable.Repeat(0f, 16_000)]);
        Assert.Equal(1.0, writer.Duration.TotalSeconds, 3);
        writer.Append([.. Enumerable.Repeat(0f, 8_000)]);
        Assert.Equal(1.5, writer.Duration.TotalSeconds, 3);

        writer.Close();
    }

    [Fact]
    public void TestDiscardRemovesFile()
    {
        var writer = MakeWriter();
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.2f, 1000)]);
        string path = writer.FilePath;
        Assert.True(File.Exists(path));

        writer.Discard();

        Assert.False(
            File.Exists(path),
            "a cancelled dictation must not leave a voice recording on disk");
    }

    [Fact]
    public void TestAppendWithoutOpenFails()
    {
        var writer = MakeWriter();

        Assert.Throws<WAVWriter.Failure.NotOpen>(() => writer.Append([0.1f]));
    }

    [Fact]
    public void TestClampsSamplesOutOfRange()
    {
        // An input that is too loud must not overflow the scale and turn into a crackle.
        var writer = MakeWriter();
        writer.Open();
        writer.Append([2.0f, -2.0f]);
        string path = writer.Close();

        byte[] data = File.ReadAllBytes(path);
        short first = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(44, 2));
        short second = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(46, 2));
        Assert.Equal(32767, first);
        Assert.Equal(-32767, second);
    }

    [Fact]
    public void TestEmptyRecordingStillProducesValidFile()
    {
        // The user pressed and immediately released - the file must be correct,
        // just empty, not broken.
        var writer = MakeWriter();
        writer.Open();
        string path = writer.Close();

        Assert.Equal(0, WavFile.Read(path).Length);
    }
}
