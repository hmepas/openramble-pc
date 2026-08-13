using System.Buffers.Binary;
using System.Text;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>
/// Reads back a recording the way the system would.
///
/// The Swift tests open the finished file with <c>AVAudioFile(forReading:)</c>: the point
/// is not the API but the promise behind it - a file that recognition can actually
/// read, with the declared rate, one channel and exactly as many frames as were
/// spoken. There is no cross-platform decoder in the base class library, so the
/// header is parsed here instead, and every field the Swift writer fills in is
/// checked against the bytes that really lie on disk. An "almost WAV" fails here
/// the same way it failed for AVAudioFile.
/// </summary>
internal sealed record WavFile(int SampleRate, int Channels, int BitsPerSample, long Length)
{
    public static WavFile Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 44, $"a WAV file is at least a 44 byte header, got {bytes.Length}");

        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));

        Assert.Equal("fmt ", Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2)));
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
        int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32, 2));
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2));

        Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4));

        // The sizes in the header must describe the file that is really on disk:
        // a header promising more than was written is read as a truncated phrase.
        Assert.Equal(bytes.Length, 44 + (int)dataSize);
        Assert.Equal(bytes.Length - 8, (int)riffSize);
        Assert.Equal((uint)(sampleRate * channels * bitsPerSample / 8), byteRate);
        Assert.Equal(channels * bitsPerSample / 8, blockAlign);
        Assert.True(blockAlign > 0, "a frame cannot be zero bytes long");

        return new WavFile(sampleRate, channels, bitsPerSample, dataSize / blockAlign);
    }
}
