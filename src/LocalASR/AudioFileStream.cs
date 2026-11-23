using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LocalASR;

/// <summary>
/// An audio or video file in bounded reads of mono 16 kHz Float32, for transcribing a file that
/// may be hours long without ever holding it whole.
/// </summary>
/// <remarks>
/// <para>
/// The Swift original reads every format through <c>AVAudioFile</c>: M4A, MP3, WAV, AIFF, FLAC,
/// Ogg, Opus and the sound of MP4 and MOV. Nothing like it ships with .NET. A RIFF/WAVE file is
/// read here by <see cref="AudioFileReader"/>'s own cursor, so the recordings this app writes
/// and plain WAV exports need nothing else. Everything else goes through <c>ffmpeg</c>, run as
/// a separate process - the decoder upstream's Linux server runs, with its command line: only
/// plain files may be opened and only the containers people actually send, so a playlist
/// pointing at a URL is refused rather than fetched, and its messages go nowhere because they
/// can quote what is inside the file. ffmpeg is optional: without it a WAV still reads, and
/// any other file fails with a message that names what to install.
/// </para>
/// <para>
/// Synchronous on purpose: every read waits on a disk or a pipe, and the caller runs it on a
/// thread of its own (<see cref="DedicatedThread"/>), as the Swift original runs its reads on
/// its own disk queue.
/// </para>
/// </remarks>
public abstract class AudioFileStream : IDisposable
{
    /// <summary>Only the two readers below; public so the app's file import can read with it.</summary>
    private protected AudioFileStream()
    {
    }

    /// <summary>Open <paramref name="path"/>; a missing or unreadable file fails here.</summary>
    public static AudioFileStream Open(string path)
    {
        bool wav;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read);
            byte[] riff = new byte[12];
            wav = probe.ReadAtLeast(riff, riff.Length, throwOnEndOfStream: false) == riff.Length
                && Encoding.ASCII.GetString(riff, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(riff, 8, 4) == "WAVE";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AudioFileReader.Failure.Unreadable(error.Message);
        }

        return wav ? new Wav(AudioFileReader.Cursor.Open(path)) : new Ffmpeg(path);
    }

    /// <summary>Up to <paramref name="frames"/> samples; empty only at the end.</summary>
    public abstract float[] Read(int frames = AudioFileReader.Cursor.ChunkFrames);

    public abstract void Dispose();

    private sealed class Wav(AudioFileReader.Cursor cursor) : AudioFileStream
    {
        public override float[] Read(int frames = AudioFileReader.Cursor.ChunkFrames)
        {
            float[] samples = new float[frames];
            int read = cursor.Read(samples);
            return read == samples.Length ? samples : samples[..read];
        }

        public override void Dispose() => cursor.Dispose();
    }

    /// <summary>ffmpeg decoding to 16 kHz mono f32 on its standard output.</summary>
    internal sealed class Ffmpeg : AudioFileStream
    {
        /// <summary>
        /// Upstream's server command line (<c>apps/cli/src/decode.rs</c>), reading the file by
        /// name instead of from standard input. <c>-ac 1</c> mixes every channel in.
        /// </summary>
        internal static readonly string[] Arguments =
        [
            "-nostdin", "-v", "error", "-threads", "1",
            "-protocol_whitelist", "file",
            "-format_whitelist", "mov,ogg,matroska,wav,mp3,flac,aac,caf,aiff,asf,amr",
        ];

        internal const string Missing =
            "this format needs ffmpeg, which was not found; install ffmpeg (for example `sudo pacman -S ffmpeg`) or convert the file to WAV";

        private readonly Process process;
        private readonly Stream output;
        private readonly byte[] carry = new byte[4];
        private int carried;
        private long total;
        private bool ended;

        public Ffmpeg(string path)
        {
            // Resolved here rather than by Process.Start, which would try the working directory
            // first - and a file is often transcribed from the folder someone else filled.
            string ffmpeg = DictationCore.ExecutablePath.Find("ffmpeg")
                ?? throw new AudioFileReader.Failure.Unreadable(Missing);
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in Arguments) { start.ArgumentList.Add(argument); }
            // "file:" so that a name that looks like a protocol is still a file name.
            foreach (string argument in new[] { "-i", "file:" + Path.GetFullPath(path), "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "-" })
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                process = Process.Start(start) ?? throw new AudioFileReader.Failure.Unreadable(Missing);
            }
            catch (Win32Exception)
            {
                throw new AudioFileReader.Failure.Unreadable(Missing);
            }

            process.StandardInput.Close();
            // Read and dropped: it can quote what is inside the file, and a full pipe would stall
            // the decoder.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            output = process.StandardOutput.BaseStream;
        }

        public override float[] Read(int frames = AudioFileReader.Cursor.ChunkFrames)
        {
            if (ended) { return []; }

            byte[] bytes = new byte[frames * 4];
            carry.AsSpan(0, carried).CopyTo(bytes);
            int filled = carried + output.ReadAtLeast(
                bytes.AsSpan(carried), bytes.Length - carried, throwOnEndOfStream: false);
            int whole = filled / 4 * 4;
            carried = filled - whole;
            bytes.AsSpan(whole, carried).CopyTo(carry);

            float[] samples = new float[whole / 4];
            for (int index = 0; index < samples.Length; index++)
            {
                float sample = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * 4, 4));
                // Anything that is not a number is silence, as on the server.
                samples[index] = float.IsFinite(sample) ? sample : 0;
            }

            total += samples.Length;
            if (filled < bytes.Length)
            {
                ended = true;
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new AudioFileReader.Failure.Unreadable("ffmpeg found no audio it could decode in the file")
                    {
                        EndedBySignal = BySignal(process.ExitCode),
                    };
                }

                if (total == 0)
                {
                    throw new AudioFileReader.Failure.EmptyFile();
                }
            }

            return samples;
        }

        /// <summary>
        /// 255 is ffmpeg's own exit after it caught SIGINT or SIGTERM; 128 + n is .NET's status
        /// for a process the signal killed outright (SIGINT, SIGKILL, SIGTERM).
        /// </summary>
        private static bool BySignal(int exitCode) => exitCode is 255 or 130 or 137 or 143;

        public override void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }
}
