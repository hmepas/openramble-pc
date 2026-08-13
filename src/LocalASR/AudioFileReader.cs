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
    }

    /// <summary>Read the entire file, resulting in 16 kHz mono.</summary>
    public float[] Samples(string path)
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

        using (handle)
        {
            Format format = ReadHeader(handle);
            long frames = format.DataByteCount / format.BlockAlign;
            if (frames <= 0)
            {
                throw new Failure.EmptyFile();
            }

            // If the file is already in the required format, no conversion is needed.
            if (format.SampleRate == TargetSampleRate
                && format.Channels == 1
                && format.IsFloat32)
            {
                return ReadDirect(handle, format, frames);
            }

            return ReadConverted(handle, format, frames);
        }
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

    /// <summary>Already mono 16 kHz Float32: hand the samples over as they lie.</summary>
    private static float[] ReadDirect(FileStream handle, Format format, long frames)
    {
        handle.Position = format.DataOffset;
        float[] samples = new float[frames];
        byte[] buffer = new byte[4 * 16_384];
        int written = 0;
        while (written < samples.Length)
        {
            int wanted = Math.Min(buffer.Length, (samples.Length - written) * 4);
            int read = handle.Read(buffer, 0, wanted);
            if (read <= 0)
            {
                break;
            }

            for (int offset = 0; offset + 4 <= read; offset += 4)
            {
                samples[written++] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset, 4));
            }
        }

        if (written == 0)
        {
            throw new Failure.EmptyFile();
        }

        return written == samples.Length ? samples : samples[..written];
    }

    /// <summary>
    /// Channel mixdown plus resampling, in chunks - an hour-long take must not be held twice
    /// in memory at once.
    ///
    /// The resampling is linear interpolation over the mixed-down mono signal. Speech at
    /// 8-48 kHz is the whole input space here and the engine's own front end is a mel
    /// filterbank, so the difference from AVAudioConverter's polyphase filter is inaudible
    /// where it matters and the sample count is identical.
    /// </summary>
    private static float[] ReadConverted(FileStream handle, Format format, long frames)
    {
        handle.Position = format.DataOffset;

        double ratio = TargetSampleRate / format.SampleRate;
        var output = new List<float>((int)Math.Min(int.MaxValue, (long)(frames * ratio) + 1024));

        // The converter walks output positions: output n sits at source position n * step.
        double step = format.SampleRate / TargetSampleRate;
        long producedOutputs = 0;
        long consumedSource = 0;
        var pending = new List<float>();

        const int ChunkFrames = 16_384;
        int bytesPerSample = format.BitsPerSample / 8;
        byte[] buffer = new byte[ChunkFrames * format.BlockAlign];
        long remainingBytes = format.DataByteCount;

        while (remainingBytes > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remainingBytes);
            int read = handle.Read(buffer, 0, wanted);
            if (read <= 0)
            {
                break;
            }

            remainingBytes -= read;
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

            Emit(output, pending, ref producedOutputs, ref consumedSource, step, atEnd: false);
        }

        Emit(output, pending, ref producedOutputs, ref consumedSource, step, atEnd: true);

        if (output.Count == 0)
        {
            throw new Failure.EmptyFile();
        }

        return [.. output];
    }

    /// <summary>
    /// Turn whatever source samples are buffered into output samples.
    ///
    /// Interpolation needs the sample after the one it lands on, so at a chunk boundary the
    /// last source sample stays in the buffer until the next chunk arrives. That is what
    /// keeps a long recording from losing a sample at every 16 384-frame seam.
    /// </summary>
    private static void Emit(
        List<float> output,
        List<float> pending,
        ref long producedOutputs,
        ref long consumedSource,
        double step,
        bool atEnd)
    {
        long available = consumedSource + pending.Count;
        while (true)
        {
            double position = producedOutputs * step;
            long index = (long)Math.Floor(position);
            if (index >= available)
            {
                break;
            }

            bool hasNext = index + 1 < available;
            if (!hasNext && !atEnd)
            {
                break;
            }

            float current = pending[(int)(index - consumedSource)];
            float next = hasNext ? pending[(int)(index + 1 - consumedSource)] : current;
            double fraction = position - index;
            output.Add((float)(current + ((next - current) * fraction)));
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
