using System.Buffers.Binary;
using System.Text;

namespace DictationCore.Audio;

/// <summary>
/// Record audio in WAV during dictation.
///
/// We write to disk, and don't keep it in memory: an hour's dictation is more than a
/// hundred megabytes, and the file at the same time survives an application crash and
/// allows recognition to be repeated if it fails.
///
/// The WAV header contains sizes that are only known at the end, so first the blank is
/// written, and upon completion the sizes are put in place.
/// </summary>
public sealed class WAVWriter
{
    /// <summary>
    /// Recording failures. The Swift original is an <c>Error</c> enum with associated
    /// values; in C# the cases are nested exception types, so <c>throw</c> stays honest.
    /// </summary>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        /// <summary>The file for the recording could not be created.</summary>
        public sealed class CannotCreateFile : Failure
        {
            public CannotCreateFile(string path)
                : base($"cannot create file: {path}")
            {
                Path = path;
            }

            public string Path { get; }
        }

        /// <summary>The sound did not reach the disk.</summary>
        public sealed class WriteFailed : Failure
        {
            public WriteFailed(string reason)
                : base(reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        /// <summary>There is no open file: the recording has not begun or is already finished.</summary>
        public sealed class NotOpen : Failure
        {
            public NotOpen()
                : base("not open")
            {
            }
        }
    }

    private const int BitsPerSample = 16;

    private readonly string path;
    private readonly int sampleRate;
    private readonly int channels;

    private readonly Lock gate = new();
    private FileStream? handle;
    private int bytesWritten;

    public WAVWriter(string path, int sampleRate = 16_000, int channels = 1)
    {
        this.path = path;
        this.sampleRate = sampleRate;
        this.channels = channels;
    }

    /// <remarks>The Swift original exposes a file <c>URL</c>; per the porting contract a file URL becomes a path.</remarks>
    public string FilePath => path;

    /// <summary>Duration of what has been recorded so far.</summary>
    public TimeSpan Duration
    {
        get
        {
            lock (gate)
            {
                int bytesPerFrame = channels * BitsPerSample / 8;
                if (bytesPerFrame <= 0 || sampleRate <= 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds((double)(bytesWritten / bytesPerFrame) / sampleRate);
            }
        }
    }

    /// <summary>Create a file and write a header template.</summary>
    public void Open()
    {
        lock (gate)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Creating the directory is best-effort, exactly as in the original:
                    // the refusal that matters is the one from creating the file.
                }
            }

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new Failure.CannotCreateFile(path);
            }

            try
            {
                stream.Write(Header(dataBytes: 0));
                stream.Flush();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                stream.Dispose();
                throw new Failure.WriteFailed(error.Message);
            }

            handle = stream;
            bytesWritten = 0;
        }
    }

    /// <summary>
    /// Add a portion of sound.
    ///
    /// Input - Float32 in the range [-1, 1], 16-bit PCM goes to disk: twice as compact
    /// and exactly what recognition expects.
    /// </summary>
    public void Append(float[] samples)
    {
        lock (gate)
        {
            if (handle is null)
            {
                throw new Failure.NotOpen();
            }

            byte[] data = new byte[samples.Length * 2];
            for (int index = 0; index < samples.Length; index++)
            {
                // A damaged device or graph can surface non-finite Float32 values. Keep the
                // WAV structurally honest and bound the impulse instead of trusting the cast.
                float sample = samples[index];
                float finite = float.IsNaN(sample) ? 0f : sample;
                float clamped = Math.Max(-1f, Math.Min(1f, finite));
                short value = (short)(clamped * 32767);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2, 2), value);
            }

            try
            {
                handle.Write(data);
                handle.Flush();
                bytesWritten += data.Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new Failure.WriteFailed(error.Message);
            }
        }
    }

    /// <summary>Close the file, putting the actual sizes in the header.</summary>
    /// <returns>The path of the finished recording.</returns>
    public string Close()
    {
        lock (gate)
        {
            if (handle is null)
            {
                throw new Failure.NotOpen();
            }

            // The handle is freed anyway. Otherwise an unsuccessful close would leave the
            // file open forever: the writer is unavailable after this, and there is no one
            // else to close it - this is how a day of dictation reaches the limit of open
            // files of the process.
            FileStream stream = handle;
            handle = null;

            try
            {
                stream.Flush(flushToDisk: true);
                // The sizes are known only now - we rewrite the entire header.
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(Header(dataBytes: bytesWritten));
                stream.Flush(flushToDisk: true);
                stream.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                stream.Dispose();
                throw new Failure.WriteFailed(error.Message);
            }

            return path;
        }
    }

    /// <summary>Abort the recording and delete the file - the user cancelled the dictation.</summary>
    public void Discard()
    {
        lock (gate)
        {
            handle?.Dispose();
            handle = null;
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Nothing can be done about a file that refuses to disappear, and the
                // dictation is already cancelled.
            }
        }
    }

    /// <summary>
    /// Release the descriptor but leave the raw take where crash recovery can repair its
    /// header on the next launch.
    ///
    /// Intentionally different from <see cref="Discard"/>: a disk failure means the file
    /// may be incomplete, but deleting the bytes that did make it to storage would turn a
    /// partial recovery into no recovery at all.
    /// </summary>
    public void AbandonForRecovery()
    {
        lock (gate)
        {
            try
            {
                handle?.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // What reached the disk stays; nothing else is owed here.
            }

            handle = null;
        }
    }

    /// <summary>44 byte WAV header.</summary>
    private byte[] Header(int dataBytes)
    {
        int byteRate = sampleRate * channels * BitsPerSample / 8;
        int blockAlign = channels * BitsPerSample / 8;

        byte[] data = new byte[44];
        Span<byte> span = data;

        Encoding.ASCII.GetBytes("RIFF", span[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVE", span[8..12]);

        Encoding.ASCII.GetBytes("fmt ", span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1); // PCM without compression
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], BitsPerSample);

        Encoding.ASCII.GetBytes("data", span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], (uint)dataBytes);
        return data;
    }
}
