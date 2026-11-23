using System.Buffers.Binary;
using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// Port of upstream RecordingPlayerTests.swift: what reaches the output, and the position,
/// pause, seek and end logic - against a fake output and a hand-driven clock.
/// </summary>
public sealed class RecordingPlayerTests : IDisposable
{
    private const int Rate = 16_000;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recording-player-" + Guid.NewGuid());
    private readonly ManualClock clock = new();
    private readonly List<FakeSink> sinks = [];
    private readonly RecordingPlayer player;

    public RecordingPlayerTests()
    {
        Directory.CreateDirectory(directory);
        player = new RecordingPlayer(PcmSources.Open, _ => Add(new FakeSink()), clock, TimeSpan.Zero);
    }

    public void Dispose()
    {
        player.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private FakeSink Add(FakeSink sink)
    {
        lock (sinks) { sinks.Add(sink); }
        return sink;
    }

    private FakeSink LastSink
    {
        get { lock (sinks) { return sinks[^1]; } }
    }

    /// <summary>Mic only, system only, then both: the same loud tone, as a stereo meeting file.</summary>
    private string Recording()
    {
        short Tone(int index) => (short)(Math.Sin(index * 0.05) * 0.9 * short.MaxValue);
        var frames = new List<(short Left, short Right)>();
        for (int index = 0; index < Rate; index++) { frames.Add((Tone(index), 0)); }
        for (int index = 0; index < Rate; index++) { frames.Add((0, Tone(index))); }
        for (int index = 0; index < Rate; index++) { frames.Add((Tone(index), Tone(index))); }

        string path = Path.Combine(directory, "audio.wav");
        using var file = new BinaryWriter(File.Create(path));
        int data = frames.Count * 4;
        file.Write("RIFF"u8);
        file.Write(36 + data);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(Rate);
        file.Write(Rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(data);
        foreach ((short left, short right) in frames)
        {
            file.Write(left);
            file.Write(right);
        }

        return path;
    }

    /// <summary>Let <paramref name="seconds"/> pass and wait until the worker has written up to it.</summary>
    private void Advance(double seconds)
    {
        clock.Advance(TimeSpan.FromSeconds(seconds));
        FakeSink sink = LastSink;
        Assert.True(SpinWait.SpinUntil(
            () => sink.Frames >= Math.Min(Rate * 3 - sink.StartOffset, (clock.Seconds - sink.StartedAt + 0.25) * Rate) || sink.Finished,
            5_000));
    }

    private static void AssertCentered(short[] samples)
    {
        int peak = samples.Max(sample => Math.Abs((int)sample));
        Assert.True(peak > 0.2 * short.MaxValue, "speech must be audible");
        Assert.True(peak < short.MaxValue, "simultaneous voices must not clip");
    }

    [Fact]
    public void Microphone_system_and_both_play_mixed_without_clipping_then_end()
    {
        int ended = 0;
        Guid endedId = Guid.Empty;
        player.Ended += (_, id) =>
        {
            endedId = id;
            Interlocked.Increment(ref ended);
        };
        var recording = Guid.NewGuid();
        player.Load(recording, Recording());
        Assert.Equal(3, player.Duration.TotalSeconds, 3);

        player.Toggle();
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(SpinWait.SpinUntil(() => !player.IsPlaying, 5_000));

        short[] audio = LastSink.Samples();
        Assert.Equal(Rate * 3, audio.Length);
        for (int second = 0; second < 3; second++)
        {
            AssertCentered(audio[(second * Rate + 4_000)..(second * Rate + 12_000)]);
        }

        Assert.True(LastSink.Finished);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, 5_000));
        Assert.Equal(recording, endedId);
        Assert.Equal(3, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public void Pause_resume_and_seek_keep_the_position()
    {
        player.Load(Guid.NewGuid(), Recording());
        player.Toggle();
        Advance(0.25);
        player.Pause();
        Assert.False(player.IsPlaying);
        Assert.True(LastSink.Aborted, "a paused player stops its output");
        Assert.Equal(0.25, player.Position.TotalSeconds, 3);

        player.Toggle();
        LastSink.StartedAt = clock.Seconds;
        LastSink.StartOffset = Rate / 4;
        Advance(0.25);
        Assert.Equal(0.5, player.Position.TotalSeconds, 3);
        player.Pause();
        Assert.Equal(0.5, player.Position.TotalSeconds, 3);

        player.Seek(TimeSpan.FromSeconds(1.25));
        Assert.Equal(1.25, player.Position.TotalSeconds, 3);
        Assert.False(player.IsPlaying);
        Assert.Equal(2, sinks.Count);

        player.Toggle();
        LastSink.StartedAt = clock.Seconds;
        LastSink.StartOffset = Rate * 5 / 4;
        Advance(0.25);
        AssertCentered(LastSink.Samples()[..(Rate / 4)]);
        player.Pause();
        Assert.Equal(1.5, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public void Seeking_while_playing_restarts_there_and_play_at_the_end_restarts_from_zero()
    {
        string path = Recording();
        player.Load(Guid.NewGuid(), path);
        player.Toggle();
        Advance(0.1);
        player.Seek(TimeSpan.FromSeconds(2.25));
        Assert.True(player.IsPlaying);
        Assert.Equal(2, sinks.Count);
        Assert.True(sinks[0].Aborted);
        LastSink.StartedAt = clock.Seconds;
        LastSink.StartOffset = Rate * 9 / 4;
        Advance(0.25);
        short[] heard = LastSink.Samples()[..100];
        short[] expected = new short[100];
        using (IPcmReader reader = PcmSources.Open(path).OpenAt(Rate * 9 / 4)) { reader.Read(expected); }
        Assert.Equal(expected, heard);

        player.Seek(TimeSpan.FromSeconds(50));
        Assert.False(player.IsPlaying);
        Assert.Equal(3, player.Position.TotalSeconds, 3);

        player.Toggle();
        Assert.True(player.IsPlaying);
        Assert.Equal(0, player.Position.TotalSeconds);
        player.Pause();
    }

    [Fact]
    public void An_old_stream_cannot_finish_a_new_selection()
    {
        int ended = 0;
        player.Ended += (_, _) => Interlocked.Increment(ref ended);
        string path = Recording();
        player.Load(Guid.NewGuid(), path);
        player.Toggle();
        Advance(0.1);

        var id = Guid.NewGuid();
        player.Load(id, path);
        clock.Advance(TimeSpan.FromSeconds(10));
        Thread.Sleep(50);
        Assert.Equal(id, player.LoadedId);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.False(player.IsPlaying);
        Assert.Equal(0, Volatile.Read(ref ended));

        player.Seek(TimeSpan.FromSeconds(1));
        player.Load(id, path);
        Assert.Equal(1, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public void The_position_trails_the_writes_by_the_output_latency()
    {
        using var delayed = new RecordingPlayer(PcmSources.Open, _ => Add(new FakeSink()), clock, TimeSpan.FromMilliseconds(150));
        delayed.Load(Guid.NewGuid(), Recording());
        delayed.Toggle();
        Advance(0.1);
        Assert.Equal(TimeSpan.Zero, delayed.Position);
        Advance(0.4);
        Assert.Equal(0.35, delayed.Position.TotalSeconds, 3);
        delayed.Pause();
        Assert.Equal(0.35, delayed.Position.TotalSeconds, 3);
    }

    [Fact]
    public void Missing_audio_is_reported_and_unloading_clears_it()
    {
        player.Load(Guid.NewGuid(), Path.Combine(directory, "missing.wav"));
        Assert.True(player.FailedToLoad);
        player.Toggle();
        Assert.False(player.IsPlaying);
        player.Unload();
        Assert.False(player.FailedToLoad);
        Assert.Null(player.LoadedId);
    }

    [Fact]
    public void A_file_with_no_audio_fails_to_load_cleanly()
    {
        string path = Path.Combine(directory, "notes.m4a");
        File.WriteAllText(path, "not audio");
        player.Load(Guid.NewGuid(), path);
        Assert.True(player.FailedToLoad);
        Assert.NotNull(player.LoadError);
    }

    [Fact]
    public void A_failed_load_is_retried_and_a_new_file_for_the_same_id_reloads()
    {
        int opens = 0;
        using var flaky = new RecordingPlayer(
            path => Interlocked.Increment(ref opens) == 1 ? throw new IOException("busy") : PcmSources.Open(path),
            _ => Add(new FakeSink()), clock, TimeSpan.Zero);
        var id = Guid.NewGuid();
        string path = Recording();

        flaky.Load(id, path);
        Assert.True(flaky.FailedToLoad);
        Assert.Null(flaky.LoadedId);

        flaky.Load(id, path);
        Assert.False(flaky.FailedToLoad);
        Assert.Equal(id, flaky.LoadedId);
        Assert.Equal(2, opens);

        flaky.Load(id, path);
        Assert.Equal(2, opens);

        string moved = Path.Combine(directory, "audio-compressed.wav");
        File.Copy(path, moved);
        flaky.Load(id, moved);
        Assert.Equal(3, opens);
        Assert.Equal(id, flaky.LoadedId);
    }

    [Fact]
    public async Task Opening_a_file_does_not_hold_the_player_and_an_unload_meanwhile_wins()
    {
        using var opening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var slow = new RecordingPlayer(
            path =>
            {
                opening.Set();
                release.Wait();
                return PcmSources.Open(path);
            },
            _ => Add(new FakeSink()), clock, TimeSpan.Zero);
        string path = Recording();

        var load = Task.Run(() => slow.Load(Guid.NewGuid(), path));
        Assert.True(opening.Wait(5_000));
        // Times out (throws) if the player's lock is held while the file is opening.
        await Task.Run(() => slow.IsPlaying).WaitAsync(TimeSpan.FromSeconds(5));
        slow.Unload();
        release.Set();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(slow.LoadedId);
        Assert.Equal(TimeSpan.Zero, slow.Duration);
    }

    [Fact]
    public void Pw_cat_gets_raw_mono_s16_at_the_files_rate_on_stdin()
    {
        Assert.Equal(
            ["--playback", "--raw", "--format", "s16", "--rate", "16000", "--channels", "1", "--media-role", "Music", "-"],
            PwCatSink.Arguments(16_000));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public double Seconds => (double)Interlocked.Read(ref ticks) / TimeSpan.TicksPerSecond;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class FakeSink : IPcmSink
    {
        private readonly List<byte> bytes = [];
        private volatile bool aborted;

        public double StartedAt { get; set; }

        public int StartOffset { get; set; }

        public bool Aborted => aborted;

        public bool Finished { get; private set; }

        public long Frames
        {
            get { lock (bytes) { return bytes.Count / 2; } }
        }

        public short[] Samples()
        {
            lock (bytes)
            {
                byte[] raw = [.. bytes];
                return [.. Enumerable.Range(0, raw.Length / 2).Select(index => BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(index * 2)))];
            }
        }

        public void Write(ReadOnlySpan<byte> pcm)
        {
            if (aborted) { throw new IOException("broken pipe"); }
            lock (bytes) { bytes.AddRange(pcm.ToArray()); }
        }

        public void Finish(CancellationToken cancellation) => Finished = true;

        public void Abort() => aborted = true;

        public void Dispose()
        {
        }
    }
}
