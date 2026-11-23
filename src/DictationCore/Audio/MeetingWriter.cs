using System.Buffers.Binary;
using System.Text;

namespace DictationCore.Audio;

/// <summary>
/// The recording on disk: one stereo WAV and its waveform, growing together.
///
/// <see cref="WAVWriter"/> needs no change for this. It is already channel-parameterised and
/// writes a flat Int16 stream, so interleaved <c>[L0, R0, L1, R1, ...]</c> with two channels
/// is correct as written - and interleaving is what makes the two sides sample-locked by the
/// file format itself. A three-sample skew between channels cannot exist in an interleaved WAV.
///
/// One failure latches everything. A writer that keeps accepting audio after a write failed
/// would let the UI say "Recording" over a dead file; the caller sees
/// <see cref="HasWriteFailure"/> and stops honestly instead.
/// </summary>
public sealed class MeetingWriter
{
    /// <summary>Meeting writer failures, as nested exception types (the Swift original is an error enum).</summary>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        public sealed class MismatchedChannels(int microphone, int system)
            : Failure($"mismatched channels: microphone {microphone}, system {system}")
        {
            public int Microphone { get; } = microphone;

            public int System { get; } = system;
        }

        public sealed class WriteFailed(string reason) : Failure(reason)
        {
            public string Reason { get; } = reason;
        }

        public sealed class NotOpen() : Failure("not open");
    }

    public const string AudioFileName = "audio.wav";
    public const string PeaksFileName = "peaks.bin";
    public const int SampleRate = 16_000;

    private const int HeaderBytes = 44;
    private const int BytesPerFrame = 4;

    private readonly WAVWriter wav;
    private readonly PeakFile peaks;
    private readonly Lock gate = new();
    private float bucketMicrophone;
    private float bucketSystem;
    private int bucketFilled;
    private long framesWritten;
    private bool failed;

    public MeetingWriter(string directory)
    {
        AudioPath = Path.Combine(directory, AudioFileName);
        PeaksPath = Path.Combine(directory, PeaksFileName);
        wav = new WAVWriter(AudioPath, SampleRate, channels: 2);
        peaks = new PeakFile(PeaksPath, channelCount: 2);
    }

    public string AudioPath { get; }

    public string PeaksPath { get; }

    public bool HasWriteFailure
    {
        get
        {
            lock (gate)
            {
                return failed;
            }
        }
    }

    /// <summary>Frames on disk so far - the recording's own clock.</summary>
    public long FrameCount
    {
        get
        {
            lock (gate)
            {
                return framesWritten;
            }
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    public void Open()
    {
        try
        {
            wav.Open();
            peaks.Open();
        }
        catch (Exception error) when (error is WAVWriter.Failure or PeakFile.Failure)
        {
            throw new Failure.WriteFailed(error.Message);
        }
    }

    /// <summary>
    /// Append one stretch of both channels. The two must be the same length: the aligner
    /// guarantees it, and anything else would put the channels out of step for the rest of
    /// the file.
    /// </summary>
    public void Append(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system)
    {
        if (microphone.Length != system.Length)
        {
            throw new Failure.MismatchedChannels(microphone.Length, system.Length);
        }

        if (microphone.IsEmpty)
        {
            return;
        }

        lock (gate)
        {
            if (failed)
            {
                throw new Failure.WriteFailed("a previous write failed");
            }

            float[] interleaved = new float[microphone.Length * 2];
            for (int index = 0; index < microphone.Length; index++)
            {
                interleaved[index * 2] = microphone[index];
                interleaved[(index * 2) + 1] = system[index];
            }

            try
            {
                wav.Append(interleaved);
                framesWritten += microphone.Length;
                AccumulatePeaks(microphone, system);
            }
            catch (Exception error) when (error is WAVWriter.Failure or PeakFile.Failure)
            {
                failed = true;
                throw new Failure.WriteFailed(error.Message);
            }
        }
    }

    /// <summary>Seal, make durable, close - the recording ended normally.</summary>
    public void Finish()
    {
        lock (gate)
        {
            try
            {
                if (bucketFilled > 0)
                {
                    peaks.Append([bucketMicrophone, bucketSystem]);
                    ResetBucket();
                }

                peaks.Close();
                wav.Close();
            }
            catch (Exception error) when (error is WAVWriter.Failure or PeakFile.Failure)
            {
                failed = true;
                throw new Failure.WriteFailed(error.Message);
            }
        }
    }

    /// <summary>
    /// Drop the descriptors without sealing - the shape a crash leaves. What reached disk
    /// stays; <see cref="RepairHeader"/> makes it playable again.
    /// </summary>
    public void Abandon()
    {
        lock (gate)
        {
            wav.AbandonForRecovery();
            peaks.Abandon();
        }
    }

    private void AccumulatePeaks(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system)
    {
        int size = peaks.FramesPerBucket;
        for (int index = 0; index < microphone.Length; index++)
        {
            bucketMicrophone = Math.Max(bucketMicrophone, Math.Abs(microphone[index]));
            bucketSystem = Math.Max(bucketSystem, Math.Abs(system[index]));
            bucketFilled++;
            if (bucketFilled == size)
            {
                peaks.Append([bucketMicrophone, bucketSystem]);
                ResetBucket();
            }
        }
    }

    private void ResetBucket()
    {
        bucketMicrophone = 0;
        bucketSystem = 0;
        bucketFilled = 0;
    }

    // MARK: - Recovery

    /// <summary>
    /// Rewrite the RIFF and data sizes from the file's actual length.
    ///
    /// A WAV that was being written when the process died has a header that still says zero
    /// bytes of audio, and every player believes it. The audio is all there after the header;
    /// only the two size fields lie.
    /// </summary>
    /// <returns>The frame count the file now declares.</returns>
    public static long RepairHeader(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new Failure.WriteFailed(error.Message);
        }

        using (stream)
        {
            try
            {
                byte[] head = new byte[HeaderBytes];
                int got = stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false);
                if (got != HeaderBytes
                    || Encoding.ASCII.GetString(head, 0, 4) != "RIFF"
                    || Encoding.ASCII.GetString(head, 8, 4) != "WAVE"
                    || Encoding.ASCII.GetString(head, 36, 4) != "data")
                {
                    throw new Failure.WriteFailed("not a WAV this app wrote");
                }

                long total = stream.Length;
                long dataBytes = Math.Max(0, total - HeaderBytes) / BytesPerFrame * BytesPerFrame;
                byte[] field = new byte[4];
                stream.Seek(4, SeekOrigin.Begin);
                BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)(36 + dataBytes));
                stream.Write(field);
                stream.Seek(40, SeekOrigin.Begin);
                BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)dataBytes);
                stream.Write(field);
                stream.Flush(flushToDisk: true);
                return dataBytes / BytesPerFrame;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new Failure.WriteFailed(error.Message);
            }
        }
    }

    /// <summary>Frames in a finished or repaired file, from its size.</summary>
    public static long FrameCountAt(string path)
    {
        long size;
        try
        {
            var info = new FileInfo(path);
            size = info.Exists ? info.Length : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            size = 0;
        }

        return Math.Max(0, size - HeaderBytes) / BytesPerFrame;
    }
}
