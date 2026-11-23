using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The Library's seekable player for one recording: play, pause, seek, a position that follows
/// what is heard, and an end.
/// </summary>
/// <remarks>
/// <para>
/// The macOS original schedules the file on an <c>AVAudioEngine</c>. Here the samples are read
/// in-process and streamed as raw mono s16 to <c>pw-cat --playback -</c>, so seeking is ours
/// rather than a player's. A stereo meeting file (L microphone, R system) is mixed to one mono
/// stream - the average of the two, so both voices at once cannot clip - and PipeWire puts it in
/// both ears.
/// </para>
/// <para>
/// <b>Every play is one stream from one frame.</b> Pause, seek and unload end it by killing
/// <c>pw-cat</c>, which silences at once and drops whatever it had buffered; the next play starts
/// a new one at the frame that was heard. Writes are paced to the wall clock, at most
/// <see cref="Lead"/> ahead of it, so the pipe never holds seconds of audio a pause would skip.
/// </para>
/// <para>
/// <b>The position is the clock, not the pipe.</b> Frames heard = time since the stream started
/// minus the output's latency, never more than was written. Good to a few tens of milliseconds,
/// which is what a transcript highlight needs.
/// </para>
/// <para>
/// WAV in 16-bit PCM is read directly. Anything else (the compressed <c>audio.m4a</c>, a float
/// WAV) is decoded by <c>ffmpeg</c> from the seek point, with <c>ffprobe</c> for the duration;
/// without them such a file fails to load with <see cref="LoadError"/> saying so.
/// </para>
/// <para>
/// <see cref="Ended"/> is raised on a worker thread; a UI marshals it, and drops it when the id it
/// carries is no longer the recording on screen - by the time it arrives another may be loaded.
/// </para>
/// </remarks>
public sealed class RecordingPlayer : IDisposable
{
    /// <summary>How far writes may run ahead of the clock.</summary>
    internal static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(250);

    /// <summary>pw-cat's start-up and its default 100 ms quantum: sound lags the first write by this.</summary>
    internal static readonly TimeSpan DefaultOutputLatency = TimeSpan.FromMilliseconds(150);

    private const int ChunkFrames = 1_600;

    private readonly Lock gate = new();
    private readonly Func<string, IPcmSource> openSource;
    private readonly Func<int, IPcmSink> openSink;
    private readonly TimeProvider clock;
    private readonly TimeSpan outputLatency;
    private IPcmSource? source;
    private Session? session;
    private long currentFrame;
    private string? loadedPath;

    /// <summary>Bumped by every load and unload, so a load that finishes late knows it was superseded.</summary>
    private long generation;

    public RecordingPlayer()
        : this(PcmSources.Open, PwCatSink.Start, TimeProvider.System, DefaultOutputLatency)
    {
    }

    internal RecordingPlayer(
        Func<string, IPcmSource> openSource, Func<int, IPcmSink> openSink, TimeProvider clock, TimeSpan outputLatency)
    {
        this.openSource = openSource;
        this.openSink = openSink;
        this.clock = clock;
        this.outputLatency = outputLatency;
    }

    /// <summary>The recording (by id) whose audio ended - not raised for pause, seek or unload.</summary>
    public event EventHandler<Guid>? Ended;

    /// <summary>The recording that loaded; <c>null</c> when none did.</summary>
    public Guid? LoadedId { get; private set; }

    /// <summary>Why the loaded recording cannot play, or <c>null</c>.</summary>
    public string? LoadError { get; private set; }

    public bool FailedToLoad => LoadError is not null;

    public TimeSpan Duration
    {
        get { lock (gate) { return source is null ? TimeSpan.Zero : ToTime(source.Frames); } }
    }

    public bool IsPlaying
    {
        get { lock (gate) { return session is not null; } }
    }

    public TimeSpan Position
    {
        get { lock (gate) { return source is null ? TimeSpan.Zero : ToTime(HeardFrame()); } }
    }

    /// <summary>
    /// Load a recording; loading the one already loaded from the same file keeps its position. A
    /// failed load is tried again, and a new file for the same id (wav compressed to m4a) reloads.
    /// </summary>
    /// <remarks>
    /// The file is opened outside the lock: probing it may run <c>ffprobe</c>, and the player's
    /// other members are read from the UI thread meanwhile.
    /// </remarks>
    public void Load(Guid id, string path)
    {
        long attempt;
        lock (gate)
        {
            if (LoadedId == id && loadedPath == path) { return; }
            UnloadLocked();
            attempt = generation;
        }

        IPcmSource? opened = null;
        string? failure = null;
        try
        {
            opened = openSource(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException or Win32Exception or InvalidOperationException)
        {
            failure = error.Message;
            DiagnosticLog.Emit("recording.play.load", ("ok", false), ("error", error.GetType().Name));
        }

        lock (gate)
        {
            if (attempt != generation)
            {
                // Another load or an unload came in while this one was opening the file.
                opened?.Dispose();
                return;
            }

            if (opened is null)
            {
                LoadError = failure;
                return;
            }

            source = opened;
            LoadedId = id;
            loadedPath = path;
        }
    }

    public void Unload()
    {
        lock (gate) { UnloadLocked(); }
    }

    /// <summary>Play from the position, or from the start when at the end; pause when playing.</summary>
    public void Toggle()
    {
        lock (gate)
        {
            if (source is null) { return; }
            if (session is not null) { PauseLocked(); }
            else { PlayFromHereLocked(); }
        }
    }

    public void Play()
    {
        lock (gate)
        {
            if (session is null) { PlayFromHereLocked(); }
        }
    }

    public void Pause()
    {
        lock (gate) { PauseLocked(); }
    }

    /// <summary>Move to <paramref name="time"/>, clamped; playing carries on from there, the end stops.</summary>
    public void Seek(TimeSpan time)
    {
        lock (gate)
        {
            if (source is null) { return; }
            bool resume = session is not null;
            StopSession();
            long frame = (long)Math.Round(Math.Clamp(time.TotalSeconds, 0, ToTime(source.Frames).TotalSeconds) * source.SampleRate);
            currentFrame = Math.Min(frame, source.Frames);
            if (resume && currentFrame < source.Frames) { PlayLocked(); }
        }
    }

    public void Dispose() => Unload();

    private void PlayFromHereLocked()
    {
        if (source is null) { return; }
        if (currentFrame >= source.Frames - source.SampleRate / 20) { currentFrame = 0; }
        PlayLocked();
    }

    private void PlayLocked()
    {
        if (source is null) { return; }
        IPcmReader? reader = null;
        IPcmSink sink;
        try
        {
            reader = source.OpenAt(currentFrame);
            sink = openSink(source.SampleRate);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or Win32Exception or InvalidOperationException)
        {
            reader?.Dispose();
            DiagnosticLog.Emit("recording.play", ("ok", false), ("error", error.Message));
            return;
        }

        var started = new Session(currentFrame, source.SampleRate, reader, sink, clock.GetTimestamp());
        session = started;
        var worker = new Thread(() => Pump(started)) { IsBackground = true, Name = "recording-player" };
        worker.Start();
    }

    private void PauseLocked()
    {
        if (session is null) { return; }
        currentFrame = HeardFrame();
        StopSession();
    }

    private void UnloadLocked()
    {
        StopSession();
        source?.Dispose();
        source = null;
        LoadedId = null;
        loadedPath = null;
        LoadError = null;
        currentFrame = 0;
        generation++;
    }

    private void StopSession()
    {
        if (session is null) { return; }
        session.Cancel();
        session = null;
    }

    private long HeardFrame()
    {
        if (session is null || source is null) { return currentFrame; }
        long elapsed = (long)((clock.GetElapsedTime(session.StartedAt) - outputLatency).TotalSeconds * source.SampleRate);
        long heard = Math.Clamp(elapsed, 0, Interlocked.Read(ref session.Written));
        return Math.Min(source.Frames, session.StartFrame + heard);
    }

    private TimeSpan ToTime(long frames) => TimeSpan.FromSeconds((double)frames / (source?.SampleRate ?? 1));

    /// <summary>The worker: read, pace, write, then let the output drain and finish.</summary>
    private void Pump(Session running)
    {
        int rate = running.Rate;
        long leadFrames = (long)(Lead.TotalSeconds * rate);
        short[] samples = new short[ChunkFrames];
        byte[] bytes = new byte[ChunkFrames * 2];
        bool reachedEnd = false;
        try
        {
            while (!running.Token.IsCancellationRequested)
            {
                long due = (long)(clock.GetElapsedTime(running.StartedAt).TotalSeconds * rate) + leadFrames;
                if (Interlocked.Read(ref running.Written) >= due)
                {
                    running.Token.WaitHandle.WaitOne(10);
                    continue;
                }

                int count = running.Reader.Read(samples);
                if (count == 0)
                {
                    reachedEnd = true;
                    break;
                }

                for (int index = 0; index < count; index++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), samples[index]);
                }

                running.Sink.Write(bytes.AsSpan(0, count * 2));
                Interlocked.Add(ref running.Written, count);
            }

            if (reachedEnd) { running.Sink.Finish(running.Token); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException
            or InvalidDataException or OperationCanceledException)
        {
            if (!running.Token.IsCancellationRequested)
            {
                DiagnosticLog.Emit("recording.play", ("ok", false), ("error", error.Message));
            }

            reachedEnd = false;
        }
        finally
        {
            running.Reader.Dispose();
            running.Sink.Dispose();
        }

        Guid? ended = null;
        lock (gate)
        {
            // Pause, seek and loading another recording also end a stream; only the one still
            // current may move the position or say the recording ended.
            if (session == running)
            {
                session = null;
                currentFrame = reachedEnd ? source?.Frames ?? 0 : running.StartFrame + Interlocked.Read(ref running.Written);
                if (reachedEnd) { ended = LoadedId; }
            }
        }

        // Outside the lock, so a handler may call back in; the id is the one captured with the end.
        if (ended is Guid id) { Ended?.Invoke(this, id); }
    }

    private sealed class Session(long startFrame, int rate, IPcmReader reader, IPcmSink sink, long startedAt)
    {
        private readonly CancellationTokenSource cancellation = new();

        public long Written;

        public int Rate { get; } = rate;

        public long StartFrame { get; } = startFrame;

        public IPcmReader Reader { get; } = reader;

        public IPcmSink Sink { get; } = sink;

        public long StartedAt { get; } = startedAt;

        public CancellationToken Token => cancellation.Token;

        public void Cancel()
        {
            cancellation.Cancel();
            Sink.Abort();
        }
    }
}

/// <summary>A recording as mono frames from any frame on.</summary>
internal interface IPcmSource : IDisposable
{
    int SampleRate { get; }

    long Frames { get; }

    IPcmReader OpenAt(long frame);
}

internal interface IPcmReader : IDisposable
{
    /// <summary>Up to <c>destination.Length</c> mono frames; 0 only at the end.</summary>
    int Read(Span<short> destination);
}

/// <summary>Where the mono s16 stream is heard.</summary>
internal interface IPcmSink : IDisposable
{
    void Write(ReadOnlySpan<byte> pcm);

    /// <summary>No more audio: return once what was written has been played.</summary>
    void Finish(CancellationToken cancellation);

    /// <summary>Silence now, from any thread; the writer's next call fails.</summary>
    void Abort();
}

internal static class PcmSources
{
    /// <summary>16-bit PCM WAV directly; anything else through ffmpeg.</summary>
    public static IPcmSource Open(string path) =>
        (IPcmSource?)WavPcmSource.TryOpen(path) ?? FfmpegPcmSource.Open(path);
}

/// <summary>A RIFF/WAVE file of 16-bit PCM, any rate, one or two (or more) channels mixed down.</summary>
internal sealed class WavPcmSource : IPcmSource
{
    private readonly string path;
    private readonly long dataOffset;
    private readonly int channels;

    private WavPcmSource(string path, long dataOffset, int channels, int sampleRate, long frames)
    {
        this.path = path;
        this.dataOffset = dataOffset;
        this.channels = channels;
        SampleRate = sampleRate;
        Frames = frames;
    }

    public int SampleRate { get; }

    public long Frames { get; }

    /// <summary><c>null</c> when the file is not a 16-bit PCM WAV; missing or unreadable throws.</summary>
    public static WavPcmSource? TryOpen(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(file);
        if (file.Length < 12) { return null; }
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") { return null; }
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") { return null; }

        int format = 0, channels = 0, rate = 0, bits = 0;
        while (file.Position + 8 <= file.Length)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            long body = file.Position;
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xFFFE && size >= 26)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    format = reader.ReadUInt16();
                }
            }
            else if (id == "data")
            {
                if (format != 1 || bits != 16 || channels <= 0 || rate <= 0) { return null; }
                // A recording cut short by a crash may still say 0 (or 0xFFFFFFFF): the data runs to the end.
                long available = file.Length - body;
                long bytes = size == 0 || size > available ? available : size;
                return new WavPcmSource(path, body, channels, rate, bytes / (2L * channels));
            }

            file.Position = body + size + (size & 1);
        }

        return null;
    }

    public IPcmReader OpenAt(long frame) => new Reader(this, Math.Clamp(frame, 0, Frames));

    public void Dispose()
    {
    }

    private sealed class Reader : IPcmReader
    {
        private readonly WavPcmSource source;
        private readonly FileStream file;
        private byte[] buffer = [];
        private long remaining;

        public Reader(WavPcmSource source, long frame)
        {
            this.source = source;
            file = new FileStream(source.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Position = source.dataOffset + frame * 2 * source.channels;
            remaining = source.Frames - frame;
        }

        public int Read(Span<short> destination)
        {
            int channels = source.channels;
            int frames = (int)Math.Min(destination.Length, remaining);
            if (frames <= 0) { return 0; }
            int length = frames * 2 * channels;
            if (buffer.Length < length) { buffer = new byte[length]; }
            int read = file.ReadAtLeast(buffer.AsSpan(0, length), length, throwOnEndOfStream: false);
            frames = read / (2 * channels);
            for (int index = 0; index < frames; index++)
            {
                int sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan((index * channels + channel) * 2));
                }

                destination[index] = (short)(sum / channels);
            }

            remaining -= frames;
            if (frames == 0) { remaining = 0; }
            return frames;
        }

        public void Dispose() => file.Dispose();
    }
}

/// <summary>
/// Any other file (the compressed <c>audio.m4a</c>): ffprobe for the length, ffmpeg decoding to
/// mono 16 kHz s16 from the seek point.
/// </summary>
internal sealed class FfmpegPcmSource(string path, long frames) : IPcmSource
{
    private const int Rate = 16_000;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public int SampleRate => Rate;

    public long Frames { get; } = frames;

    public static FfmpegPcmSource Open(string path)
    {
        if (!File.Exists(path)) { throw new FileNotFoundException("the recording's audio is not on disk"); }
        string ffprobe = DictationCore.ExecutablePath.Find("ffprobe")
            ?? throw new NotSupportedException("this audio needs ffmpeg to play; install ffmpeg (for example `sudo pacman -S ffmpeg`)");
        var start = Start(ffprobe, [
            "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1",
            "file:" + Path.GetFullPath(path),
        ]);
        using Process probe = Process.Start(start) ?? throw new NotSupportedException("ffprobe did not start");
        probe.BeginErrorReadLine();
        Task<string> reading = probe.StandardOutput.ReadToEndAsync();
        if (!probe.WaitForExit(ProbeTimeout))
        {
            try
            {
                probe.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            throw new IOException("ffprobe did not answer in time");
        }

        // Without a timeout this also waits for the redirected streams to drain.
        probe.WaitForExit();
        string output = reading.GetAwaiter().GetResult();
        if (probe.ExitCode != 0
            || !double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !(seconds > 0))
        {
            throw new InvalidDataException("this file has no audio that can be played");
        }

        return new FfmpegPcmSource(path, (long)(seconds * Rate));
    }

    public IPcmReader OpenAt(long frame)
    {
        string ffmpeg = DictationCore.ExecutablePath.Find("ffmpeg")
            ?? throw new NotSupportedException("this audio needs ffmpeg to play");
        var start = Start(ffmpeg, [
            "-nostdin", "-v", "error", "-protocol_whitelist", "file",
            "-ss", ((double)frame / Rate).ToString("0.000", CultureInfo.InvariantCulture),
            "-i", "file:" + Path.GetFullPath(path), "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-",
        ]);
        Process process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start");
        process.BeginErrorReadLine();
        return new Reader(process);
    }

    public void Dispose()
    {
    }

    private static ProcessStartInfo Start(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        return start;
    }

    private sealed class Reader(Process process) : IPcmReader
    {
        private readonly Stream output = process.StandardOutput.BaseStream;
        private byte[] buffer = [];
        private int carried;

        public int Read(Span<short> destination)
        {
            int length = destination.Length * 2;
            if (buffer.Length < length) { Array.Resize(ref buffer, length); }
            int filled = carried + output.ReadAtLeast(buffer.AsSpan(carried, length - carried), length - carried, throwOnEndOfStream: false);
            int frames = filled / 2;
            for (int index = 0; index < frames; index++)
            {
                destination[index] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index * 2));
            }

            carried = filled - frames * 2;
            if (carried > 0) { buffer[0] = buffer[frames * 2]; }
            return frames;
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) { process.Kill(); }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // Already gone - or going: this runs in the worker's finally and must not throw.
            }

            process.Dispose();
        }
    }
}

/// <summary><c>pw-cat --playback</c> reading raw mono s16 from its standard input.</summary>
internal sealed class PwCatSink : IPcmSink
{
    private readonly Process process;
    private readonly Stream input;

    private PwCatSink(Process process)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
    }

    internal static string[] Arguments(int sampleRate) =>
        ["--playback", "--raw", "--format", "s16", "--rate", sampleRate.ToString(CultureInfo.InvariantCulture),
            "--channels", "1", "--media-role", "Music", "-"];

    public static IPcmSink Start(int sampleRate)
    {
        // The absolute path, so the working directory is never searched for a player.
        string executable = DictationCore.ExecutablePath.Find("pw-cat")
            ?? throw new NotSupportedException("pw-cat was not found; it ships with PipeWire");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in Arguments(sampleRate)) { start.ArgumentList.Add(argument); }
        Process process = Process.Start(start) ?? throw new InvalidOperationException("pw-cat did not start");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new PwCatSink(process);
    }

    public void Write(ReadOnlySpan<byte> pcm) => input.Write(pcm);

    public void Finish(CancellationToken cancellation)
    {
        input.Close();
        while (!process.WaitForExit(50)) { cancellation.ThrowIfCancellationRequested(); }
    }

    public void Abort()
    {
        try
        {
            if (!process.HasExited) { process.Kill(); }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Abort();
        try
        {
            input.Dispose();
        }
        catch (IOException)
        {
            // The pipe broke with the kill.
        }

        process.Dispose();
    }
}
