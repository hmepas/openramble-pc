using System.Buffers.Binary;
using System.Text;

namespace DictationCore.Audio;

/// <summary>
/// The waveform, precomputed while recording.
///
/// One byte per channel per 100 ms - the loudest sample in that bucket, never the mean,
/// because a mean hides a shout inside a quiet minute and this file's job is to not hide
/// things. Two hours is 144 kB. It is written as the recording goes, so a crash leaves a
/// waveform and a scrub never has to read 460 MB of audio.
///
/// Layout: <c>"ORPK"</c>, version, channel count, frames per bucket, bucket count, then the
/// buckets interleaved by channel. The count in the header is patched at close; a file that
/// never closed reads its count from its size instead.
/// </summary>
public sealed class PeakFile
{
    /// <summary>Peak file failures, as nested exception types (the Swift original is an error enum).</summary>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        public sealed class CannotCreate(string path) : Failure($"cannot create file: {path}")
        {
            public string Path { get; } = path;
        }

        public sealed class WriteFailed(string reason) : Failure(reason)
        {
            public string Reason { get; } = reason;
        }

        public sealed class NotOpen() : Failure("not open");

        public sealed class Malformed(string reason) : Failure(reason)
        {
            public string Reason { get; } = reason;
        }
    }

    /// <summary>A read-back peak file: one array per channel, values in 0...1.</summary>
    public sealed record Contents(int FramesPerBucket, float[][] Channels);

    public const ushort Version = 1;
    public const int HeaderBytes = 16;

    /// <summary>100 ms at 16 kHz.</summary>
    public const int DefaultFramesPerBucket = 1_600;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORPK");

    private readonly Lock gate = new();
    private FileStream? handle;
    private int bucketCount;

    public PeakFile(string path, int channelCount = 2, int framesPerBucket = DefaultFramesPerBucket)
    {
        FilePath = path;
        ChannelCount = channelCount;
        FramesPerBucket = framesPerBucket;
    }

    public string FilePath { get; }

    public int ChannelCount { get; }

    public int FramesPerBucket { get; }

    public void Open()
    {
        lock (gate)
        {
            if (handle is not null)
            {
                throw new Failure.NotOpen();
            }

            FileStream stream;
            try
            {
                stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new Failure.CannotCreate(FilePath);
            }

            try
            {
                stream.Write(Header(bucketCount: 0));
                stream.Flush();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                stream.Dispose();
                throw new Failure.WriteFailed(error.Message);
            }

            handle = stream;
            bucketCount = 0;
        }
    }

    /// <summary>One bucket: a peak per channel in 0...1.</summary>
    public void Append(float[] peaks)
    {
        lock (gate)
        {
            if (handle is null)
            {
                throw new Failure.NotOpen();
            }

            if (peaks.Length != ChannelCount)
            {
                throw new Failure.WriteFailed($"expected {ChannelCount} peaks, got {peaks.Length}");
            }

            byte[] bytes = new byte[peaks.Length];
            for (int index = 0; index < peaks.Length; index++)
            {
                float clamped = Math.Max(0f, Math.Min(1f, peaks[index]));
                bytes[index] = (byte)Math.Round(clamped * 255, MidpointRounding.AwayFromZero);
            }

            try
            {
                // Flushed per bucket so a crash leaves every bucket that was computed.
                handle.Write(bytes);
                handle.Flush();
                bucketCount++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new Failure.WriteFailed(error.Message);
            }
        }
    }

    public void Close()
    {
        lock (gate)
        {
            if (handle is null)
            {
                throw new Failure.NotOpen();
            }

            FileStream stream = handle;
            handle = null;
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(Header(bucketCount));
                stream.Flush(flushToDisk: true);
                stream.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                stream.Dispose();
                throw new Failure.WriteFailed(error.Message);
            }
        }
    }

    /// <summary>Release the descriptor without patching the count - what a crash does.</summary>
    public void Abandon()
    {
        lock (gate)
        {
            try
            {
                handle?.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // What reached the disk stays.
            }

            handle = null;
        }
    }

    public static Contents Read(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new Failure.Malformed("unreadable");
        }

        if (data.Length < HeaderBytes || !data.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new Failure.Malformed("not a peak file");
        }

        int channelCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
        uint framesPerBucket = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (channelCount <= 0 || framesPerBucket == 0 || framesPerBucket > int.MaxValue)
        {
            throw new Failure.Malformed("empty channel or bucket size");
        }

        // The header count is what was patched at close. A file that never closed has a zero
        // there and every bucket that reached disk after it, so the size is the truth either way.
        int available = (data.Length - HeaderBytes) / channelCount;
        float[][] channels = new float[channelCount][];
        for (int channel = 0; channel < channelCount; channel++)
        {
            channels[channel] = new float[available];
        }

        for (int bucket = 0; bucket < available; bucket++)
        {
            int baseOffset = HeaderBytes + (bucket * channelCount);
            for (int channel = 0; channel < channelCount; channel++)
            {
                channels[channel][bucket] = data[baseOffset + channel] / 255f;
            }
        }

        return new Contents((int)framesPerBucket, channels);
    }

    private byte[] Header(int bucketCount)
    {
        byte[] data = new byte[HeaderBytes];
        Span<byte> span = data;
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], (ushort)ChannelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], (uint)FramesPerBucket);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], (uint)bucketCount);
        return data;
    }
}
