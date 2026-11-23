using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace DictationCore.Audio;

/// <summary>
/// Reads one channel of one stretch of <c>audio.wav</c>, straight from the bytes.
///
/// Deliberately not a WAV decoder. While a recording runs, the WAV header still says zero
/// bytes of audio and a decoder believes it; the frames after the header are there
/// regardless, and reading them at a computed offset is coherent through the page cache
/// without an fsync. Nothing is locked, so there is nothing to contend with.
///
/// Frame <c>F</c> of a 44-byte-header stereo 16-bit WAV lives at byte <c>44 + 4F</c>.
/// </summary>
public readonly struct MeetingPcmWindowReader
{
    /// <summary>Window read failures, as nested exception types (the Swift original is an error enum).</summary>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        public sealed class CannotOpen(string path) : Failure($"cannot open: {path}")
        {
            public string Path { get; } = path;
        }

        public sealed class ShortRead(int expectedBytes, int gotBytes)
            : Failure($"short read: expected {expectedBytes} bytes, got {gotBytes}")
        {
            public int ExpectedBytes { get; } = expectedBytes;

            public int GotBytes { get; } = gotBytes;
        }

        public sealed class UnknownChannel() : Failure("unknown channel");
    }

    public const int HeaderBytes = 44;

    private static readonly MeetingChannel[] DefaultLayout = [MeetingChannel.Microphone, MeetingChannel.System];

    public MeetingPcmWindowReader(string path, IReadOnlyList<MeetingChannel>? channelLayout = null)
    {
        FilePath = path;
        ChannelLayout = channelLayout ?? DefaultLayout;
    }

    public string FilePath { get; }

    public IReadOnlyList<MeetingChannel> ChannelLayout { get; }

    public float[] Read(MeetingSegmentRef segment) =>
        Read(segment.Channel, segment.StartFrame, segment.FrameCount);

    public float[] Read(MeetingChannel channel, int startFrame, int frameCount)
    {
        int index = -1;
        for (int candidate = 0; candidate < ChannelLayout.Count; candidate++)
        {
            if (ChannelLayout[candidate] == channel)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            throw new Failure.UnknownChannel();
        }

        if (frameCount <= 0)
        {
            return [];
        }

        int channels = ChannelLayout.Count;
        SafeFileHandle descriptor;
        try
        {
            descriptor = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new Failure.CannotOpen(FilePath);
        }

        int bytes = frameCount * channels * sizeof(short);
        byte[] interleaved = new byte[bytes];
        int got = 0;
        using (descriptor)
        {
            long offset = HeaderBytes + ((long)startFrame * channels * sizeof(short));
            while (got < bytes)
            {
                int read = RandomAccess.Read(descriptor, interleaved.AsSpan(got), offset + got);
                if (read <= 0)
                {
                    break;
                }

                got += read;
            }
        }

        if (got != bytes)
        {
            throw new Failure.ShortRead(bytes, got);
        }

        float[] samples = new float[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            int at = ((frame * channels) + index) * sizeof(short);
            samples[frame] = BinaryPrimitives.ReadInt16LittleEndian(interleaved.AsSpan(at, 2)) / 32_768f;
        }

        return samples;
    }
}
