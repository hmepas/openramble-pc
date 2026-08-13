using System.Buffers.Binary;
using System.Text;

namespace LocalASR.Tests;

/// <summary>
/// Writes the take the Swift tests record with <c>AVAudioFile(forWriting:)</c>.
///
/// The point of the fixture is not the API but what lands on disk: a Float32 PCM WAV of the
/// requested rate and channel count, holding a sine rather than silence - otherwise "read the
/// file" and "read zeros" would look the same.
/// </summary>
internal static class WavFixture
{
    public static string WriteFloat32(
        string directory,
        double seconds,
        double sampleRate,
        int channels,
        string name = "take")
    {
        string path = Path.Combine(directory, $"{name}-{Guid.NewGuid():D}.wav");
        int frames = (int)(sampleRate * seconds);
        int bytesPerSample = 4;
        int blockAlign = channels * bytesPerSample;
        int dataBytes = frames * blockAlign;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header.Slice(8, 4));
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);
        // 3 is IEEE float, which is what AVAudioFormat's Float32 common format writes.
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), 32);
        Encoding.ASCII.GetBytes("data").CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), (uint)dataBytes);
        file.Write(header);

        byte[] frame = new byte[blockAlign];
        for (int index = 0; index < frames; index++)
        {
            float value = (float)(Math.Sin(index * 0.03) * 0.4);
            for (int channel = 0; channel < channels; channel++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    frame.AsSpan(channel * bytesPerSample, bytesPerSample),
                    value);
            }

            file.Write(frame);
        }

        return path;
    }
}
