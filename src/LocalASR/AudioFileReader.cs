using System.Buffers.Binary;
using System.Text;

namespace LocalASR;

/// <summary>
/// Reading an audio file into the format recognition expects: mono, 16 kHz, Float32.
///
/// Capture can hand ordinary takes to recognition as ready PCM. This reader is the durable
/// fallback for long recordings, recovery files, and external WAVs: an hour-long dictation is
/// hundreds of megabytes as Float32, while the file also survives a failure and permits retry.
/// </summary>
/// <remarks>
/// The Swift original leans on AVFoundation: <c>AVAudioFile</c> for decoding and
/// <c>AVAudioConverter</c> for the sample-rate and channel conversion. Neither exists here
/// and the base class library ships no decoder, so the WAV container is parsed directly and
/// the conversion is done by hand. The scope is deliberately the same one dictation needs:
/// PCM WAV (integer or IEEE float), any rate, any channel count. Anything else is reported as
/// unreadable rather than guessed at - the original refuses just as loudly.
/// </remarks>
public sealed class AudioFileReader
{
    /// <summary>The frequency at which Parakeet operates.</summary>
    public const double TargetSampleRate = 16_000;

    /// <remarks>
    /// The Swift original is an <c>Error</c> enum with associated values; here the cases are
    /// nested exception types so that <c>throw</c> stays honest.
    /// </remarks>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        public sealed class Unreadable : Failure
        {
            public Unreadable(string reason)
                : base(reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        public sealed class EmptyFile : Failure
        {
            public EmptyFile()
                : base("the recording is empty")
            {
            }
        }

        public sealed class ConversionFailed : Failure
        {
            public ConversionFailed(string reason)
                : base(reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        /// <summary>
        /// The header claims more audio than the caller is willing to hold. Rejected before a
        /// single sample is allocated, so oversized metadata cannot become an oversized buffer.
        /// </summary>
        public sealed class DurationExceeded : Failure
        {
            public DurationExceeded(TimeSpan actual, TimeSpan maximum)
                : base($"the recording is {actual.TotalSeconds:0.##} s long, more than the {maximum.TotalSeconds:0.##} s allowed")
            {
                Actual = actual;
                Maximum = maximum;
            }

            public TimeSpan Actual { get; }

            public TimeSpan Maximum { get; }
        }
    }

    /// <summary>
    /// <see cref="Samples"/> on a thread of its own.
    /// </summary>
    /// <remarks>
    /// The app's file-backed dictation path must not block a pool thread while restoring PCM
    /// whose in-memory copy was released during capture: a pool thread blocked on a disk is not
    /// yielded but lost, and the whole dictation waits behind it.
    /// </remarks>
    public async Task<float[]> SamplesOnDiskThread(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float[] samples = await DedicatedThread.Run(() => Samples(path), "OpenRamble recording read")
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return samples;
    }

    /// <summary>Read the entire file, resulting in 16 kHz mono.</summary>
    /// <param name="maximumDuration">
    /// Refuse a recording longer than this, judged by its header, before the sample buffer is
    /// built. <c>null</c> - no limit.
    /// </param>
    public float[] Samples(string path, TimeSpan? maximumDuration = null)
    {
        using Cursor cursor = Cursor.Open(path, maximumDuration);
        float[] samples = new float[cursor.ExpectedSamples];
        int written = 0;
        while (true)
        {
            if (written == samples.Length)
            {
                // The estimate is an upper bound; this only guards it.
                Array.Resize(ref samples, samples.Length + Cursor.ChunkFrames);
            }

            int read = cursor.Read(samples.AsSpan(written));
            if (read == 0)
            {
                break;
            }

            written += read;
        }

        if (written == 0)
        {
            throw new Failure.EmptyFile();
        }

        return written == samples.Length ? samples : samples[..written];
    }

    /// <summary>What the <c>fmt </c> chunk said, plus where the samples live.</summary>
    private readonly record struct Format(
        int Channels,
        int SampleRate,
        int BitsPerSample,
        bool IsFloat,
        long DataOffset,
        long DataByteCount)
    {
        public int BlockAlign => Channels * (BitsPerSample / 8);

        public bool IsFloat32 => IsFloat && BitsPerSample == 32;
    }

    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    private static Format ReadHeader(FileStream handle)
    {
        byte[] riff = new byte[12];
        if (!TryReadExactly(handle, riff)
            || Encoding.ASCII.GetString(riff, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(riff, 8, 4) != "WAVE")
        {
            throw new Failure.Unreadable("not a RIFF/WAVE file");
        }

        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        bool isFloat = false;
        bool sawFormat = false;

        byte[] header = new byte[8];
        while (TryReadExactly(handle, header))
        {
            string id = Encoding.ASCII.GetString(header, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));

            if (id == "fmt ")
            {
                if (size < 16)
                {
                    throw new Failure.Unreadable("the format chunk is truncated");
                }

                byte[] chunk = new byte[size];
                if (!TryReadExactly(handle, chunk))
                {
                    throw new Failure.Unreadable("the format chunk is truncated");
                }

                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(14, 2));
                if (tag == FormatExtensible && size >= 40)
                {
                    // The real format sits in the sub-format GUID's first two bytes.
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(24, 2));
                }

                isFloat = tag == FormatIeeeFloat;
                if (tag is not (FormatPcm or FormatIeeeFloat))
                {
                    throw new Failure.Unreadable($"unsupported WAV encoding {tag}");
                }

                sawFormat = true;
                continue;
            }

            if (id == "data")
            {
                if (!sawFormat)
                {
                    throw new Failure.Unreadable("the data chunk comes before the format chunk");
                }

                if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || bitsPerSample % 8 != 0)
                {
                    throw new Failure.Unreadable("the format chunk describes no sound");
                }

                long available = handle.Length - handle.Position;
                // A recorder killed mid-take leaves a header promising more than was
                // written. The bytes that did land are still speech worth recognizing.
                long dataBytes = Math.Min(size, available);
                return new Format(
                    channels,
                    sampleRate,
                    bitsPerSample,
                    isFloat,
                    handle.Position,
                    dataBytes - (dataBytes % (channels * (bitsPerSample / 8))));
            }

            // Any other chunk (LIST, fact, ...) is skipped, padded to an even length.
            long skip = size + (size % 2);
            if (handle.Seek(skip, SeekOrigin.Current) > handle.Length)
            {
                break;
            }
        }

        throw new Failure.Unreadable("the file has no audio data");
    }

    private static bool TryReadExactly(FileStream handle, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = handle.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>
    /// A WAV file read a block at a time, in 16 kHz mono.
    ///
    /// Channel mixdown plus resampling, in chunks - an hour-long take must not be held twice in
    /// memory at once, and an imported file must not be held whole at all. The mixdown and the
    /// resampler keep their state across reads, so reading in blocks of any size gives exactly
    /// the samples one whole read gives, the resampler's final samples included.
    ///
    /// The resampling is linear interpolation over the mixed-down mono signal. Speech at
    /// 8-48 kHz is the whole input space here and the engine's own front end is a mel
    /// filterbank, so the difference from AVAudioConverter's polyphase filter is inaudible
    /// where it matters and the sample count is identical. The mixdown averages the channels: a
    /// meeting file can carry the other speaker only on the right.
    /// </summary>
    /// <remarks>
    /// The Swift original's <c>Cursor</c> wraps <c>AVAudioFile</c> plus <c>AVAudioConverter</c>;
    /// here it is the hand-made WAV reading this type always did, cut into blocks.
    /// </remarks>
    internal sealed class Cursor : IDisposable
    {
        public const int ChunkFrames = 16_384;

        private readonly FileStream handle;
        private readonly Format format;

        /// <summary>Already mono 16 kHz Float32: the samples are handed over as they lie.</summary>
        private readonly bool direct;

        /// <summary>The converter walks output positions: output n sits at source position n * step.</summary>
        private readonly double step;

        private readonly byte[] buffer;
        private readonly List<float> pending = [];
        private long remainingBytes;
        private long producedOutputs;
        private long consumedSource;
        private bool sourceEnded;

        private Cursor(FileStream handle, Format format, long frames)
        {
            this.handle = handle;
            this.format = format;
            direct = format.SampleRate == TargetSampleRate && format.Channels == 1 && format.IsFloat32;
            step = format.SampleRate / TargetSampleRate;
            buffer = new byte[ChunkFrames * format.BlockAlign];
            remainingBytes = format.DataByteCount;
            ExpectedSamples = direct ? frames : (long)Math.Ceiling(frames / step) + 1;
            handle.Position = format.DataOffset;
        }

        /// <summary>At least as many samples as the whole file reads into.</summary>
        public long ExpectedSamples { get; }

        public static Cursor Open(string path, TimeSpan? maximumDuration = null)
        {
            FileStream handle;
            try
            {
                handle = new FileStream(path, FileMode.Open, FileAccess.Read);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new Failure.Unreadable(error.Message);
            }

            try
            {
                Format format = ReadHeader(handle);
                long frames = format.DataByteCount / format.BlockAlign;
                if (frames <= 0)
                {
                    throw new Failure.EmptyFile();
                }

                if (format.SampleRate <= 0)
                {
                    throw new Failure.ConversionFailed("the source sample rate is invalid");
                }

                var duration = TimeSpan.FromSeconds((double)frames / format.SampleRate);
                if (maximumDuration is { } maximum && duration > maximum)
                {
                    throw new Failure.DurationExceeded(duration, maximum);
                }

                return new Cursor(handle, format, frames);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>Up to <c>destination.Length</c> samples; 0 only at the end.</summary>
        public int Read(Span<float> destination)
        {
            if (destination.IsEmpty)
            {
                return 0;
            }

            if (direct)
            {
                return ReadDirect(destination);
            }

            int count = 0;
            while (true)
            {
                count += Emit(destination[count..]);
                if (count == destination.Length || sourceEnded)
                {
                    return count;
                }

                ReadSource();
            }
        }

        public void Dispose() => handle.Dispose();

        private int ReadDirect(Span<float> destination)
        {
            int wanted = (int)Math.Min(Math.Min((long)destination.Length * 4, buffer.Length), remainingBytes);
            if (wanted <= 0)
            {
                return 0;
            }

            int read = handle.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            // A short read is the end of the file: a recorder killed mid-take.
            remainingBytes = read < wanted ? 0 : remainingBytes - read;
            int samples = read / 4;
            for (int index = 0; index < samples; index++)
            {
                destination[index] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(index * 4, 4));
            }

            return samples;
        }

        /// <summary>One block of source frames, mixed down into <see cref="pending"/>.</summary>
        private void ReadSource()
        {
            int wanted = (int)Math.Min(buffer.Length, remainingBytes);
            int read = wanted > 0
                ? handle.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false)
                : 0;
            remainingBytes = read < wanted || read == 0 ? 0 : remainingBytes - read;
            sourceEnded = remainingBytes == 0;

            int bytesPerSample = format.BitsPerSample / 8;
            int wholeFrames = read / format.BlockAlign;
            for (int frame = 0; frame < wholeFrames; frame++)
            {
                double sum = 0;
                int frameOffset = frame * format.BlockAlign;
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    sum += Decode(
                        buffer.AsSpan(frameOffset + (channel * bytesPerSample), bytesPerSample),
                        format.IsFloat);
                }

                pending.Add((float)(sum / format.Channels));
            }
        }

        /// <summary>
        /// Turn whatever source samples are buffered into output samples, at most as many as
        /// <paramref name="destination"/> holds.
        ///
        /// Interpolation needs the sample after the one it lands on, so until the source has
        /// ended the last source sample stays in the buffer for the next block. That is what
        /// keeps a long recording from losing a sample at every 16 384-frame seam, and the file
        /// from losing its final samples.
        /// </summary>
        private int Emit(Span<float> destination)
        {
            long available = consumedSource + pending.Count;
            int count = 0;
            while (count < destination.Length)
            {
                double position = producedOutputs * step;
                long index = (long)Math.Floor(position);
                if (index >= available)
                {
                    break;
                }

                bool hasNext = index + 1 < available;
                if (!hasNext && !sourceEnded)
                {
                    break;
                }

                float current = pending[(int)(index - consumedSource)];
                float next = hasNext ? pending[(int)(index + 1 - consumedSource)] : current;
                double fraction = position - index;
                destination[count++] = (float)(current + ((next - current) * fraction));
                producedOutputs++;
            }

            // Everything before the sample the next output needs will never be read again.
            long keepFrom = Math.Min((long)Math.Floor(producedOutputs * step), available);
            int drop = (int)(keepFrom - consumedSource);
            if (drop > 0)
            {
                pending.RemoveRange(0, Math.Min(drop, pending.Count));
                consumedSource += drop;
            }

            return count;
        }
    }

    private static double Decode(ReadOnlySpan<byte> sample, bool isFloat)
    {
        if (isFloat)
        {
            return sample.Length == 8
                ? BinaryPrimitives.ReadDoubleLittleEndian(sample)
                : BinaryPrimitives.ReadSingleLittleEndian(sample);
        }

        return sample.Length switch
        {
            1 => (sample[0] - 128) / 128.0,
            2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
            3 => ((sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16))) / 8_388_608.0,
            4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2_147_483_648.0,
            _ => 0,
        };
    }
}
