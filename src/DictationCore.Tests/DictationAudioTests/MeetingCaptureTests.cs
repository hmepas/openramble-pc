using System.Buffers.Binary;
using System.Diagnostics;
using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>A source that delivers what the test says, when the test says.</summary>
internal sealed class ScriptedAudioSource : IMeetingAudioSource
{
    private readonly Lock gate = new();
    private Action<MeetingAudioBlock>? onBlock;
    private Action<MeetingSourceFailure>? onFailure;
    private readonly List<Action<MeetingSourceFailure>> failureHandlers = [];
    private int startCount;
    private int stopCount;
    private int recoveryCount;

    /// <summary>Thrown by the next <see cref="Start"/>, as a device that refuses would.</summary>
    public MeetingSourceFailure? StartError { get; set; }

    public string? DeviceName => "Scripted";

    public int StartCount { get { lock (gate) { return startCount; } } }

    public int StopCount { get { lock (gate) { return stopCount; } } }

    public int RecoveryCount { get { lock (gate) { return recoveryCount; } } }

    public bool IsRunning { get { lock (gate) { return onBlock is not null; } } }

    public void Start(Action<MeetingAudioBlock> onBlock, Action<MeetingSourceFailure> onFailure)
    {
        lock (gate)
        {
            if (StartError is { } error) { throw new MeetingSourceException(error); }
            startCount++;
            this.onBlock = onBlock;
            this.onFailure = onFailure;
            failureHandlers.Add(onFailure);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            stopCount++;
            onBlock = null;
        }
    }

    public void Deliver(float[] samples, ulong? hostNanoseconds = null)
    {
        Action<MeetingAudioBlock>? block;
        lock (gate) { block = onBlock; }
        block?.Invoke(new MeetingAudioBlock(hostNanoseconds, samples));
    }

    public void Fail(MeetingSourceFailure failure)
    {
        Action<MeetingSourceFailure>? handler;
        lock (gate) { handler = onFailure; }
        handler?.Invoke(failure);
    }

    /// <summary>Report a failure through the handler of an earlier run (1-based), as a queued one would arrive.</summary>
    public void FailRun(int run, MeetingSourceFailure failure)
    {
        Action<MeetingSourceFailure> handler;
        lock (gate) { handler = failureHandlers[run - 1]; }
        handler(failure);
    }

    public void PrepareForRecovery()
    {
        lock (gate) { recoveryCount++; }
    }
}

internal sealed class AlignedAudioSink : IMeetingAudioBlockSink
{
    public sealed record Block(float[] Microphone, float[] System, long StartFrame);

    private readonly Lock gate = new();
    private readonly List<(ulong HostNanoseconds, long Frame)> anchors = [];
    private readonly List<Block> blocks = [];

    public List<(ulong HostNanoseconds, long Frame)> Anchors { get { lock (gate) { return [.. anchors]; } } }

    public List<Block> Blocks { get { lock (gate) { return [.. blocks]; } } }

    public void Receive(float[] microphone, float[] system, long startFrame)
    {
        lock (gate) { blocks.Add(new Block(microphone, system, startFrame)); }
    }

    public void Anchor(ulong hostNanoseconds, long frame)
    {
        lock (gate) { anchors.Add((hostNanoseconds, frame)); }
    }
}

/// <summary>
/// Not ported, reason:
/// <list type="bullet">
/// <item><c>testScreenOnlyCaptureAdvancesASilentTimelineWithoutHardware</c> - screen recording
/// (and its silent clock for a take with no audio sources) is macOS-only and not in the port.</item>
/// <item><c>testTheProbePlaysWhenTheOtherSideIsCapturedAndNotForAVoiceNote</c> - the inaudible
/// probe tone proves a macOS tap's permission; Linux has no such failure mode and drops the probe
/// (wave 4b decision). The route-change test below keeps everything but the probe count.</item>
/// </list>
/// </summary>
public sealed class MeetingCaptureTests : IDisposable
{
    private readonly string directory;

    public MeetingCaptureTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"meeting-capture-{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static float[] Constant(float value, int count) => [.. Enumerable.Repeat(value, count)];

    /// <summary>The Swift test reads with AVAudioFile; here the header is validated by <see cref="WavFile"/>.</summary>
    private static (float[] Left, float[] Right) ReadChannels(string path)
    {
        WavFile wav = WavFile.Read(path);
        Assert.Equal(2, wav.Channels);
        byte[] bytes = File.ReadAllBytes(path);
        int frames = (int)wav.Length;
        float[] left = new float[frames];
        float[] right = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            left[frame] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (frame * 4), 2)) / 32_768f;
            right[frame] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46 + (frame * 4), 2)) / 32_768f;
        }
        return (left, right);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (watch.Elapsed > limit) { Assert.Fail("timed out waiting"); }
            await Task.Delay(10);
        }
    }

    private static MeetingCapture.Failure FailureOf(Action action) =>
        Assert.Throws<MeetingCaptureException>(action).Failure;

    [Fact]
    public void TestAVoiceNoteRecordsTheMicrophoneLeftAndSilenceRight()
    {
        var microphone = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        capture.Start();
        Assert.Equal(MeetingCapture.CaptureState.Recording, capture.State);
        for (int i = 0; i < 3; i++) { microphone.Deliver(Constant(0.5f, 1_600)); }
        MeetingCapture.Summary summary = capture.Stop();

        Assert.Equal(4_800, summary.FrameCount);
        Assert.Equal(0.3, summary.Duration, 0.0001);
        Assert.False(summary.SystemAudio.WasRequested);
        Assert.Equal(MeetingEndReason.StoppedByUser, summary.EndReason);
        Assert.Equal("Scripted", summary.MicrophoneDeviceName);
        Assert.Equal(1, microphone.StopCount);
        Assert.Equal(MeetingCapture.CaptureState.Stopped, capture.State);

        var audio = ReadChannels(capture.AudioPath);
        Assert.Equal(4_800, audio.Left.Length);
        Assert.Equal(0.5, audio.Left[100], 0.001);
        Assert.Equal(0, audio.Right[100], 0.001);
        Assert.True(File.Exists(Path.Combine(directory, "peaks.bin")));
    }

    [Fact]
    public void TestAlignedAudioSinkUsesTheWAVClockAfterAppend()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var sink = new AlignedAudioSink();
        var capture = new MeetingCapture(directory, microphone, system, audioSink: sink);
        capture.Start();
        microphone.Deliver(Constant(0.25f, 1_600));
        system.Deliver(Constant(-0.5f, 1_600));
        _ = capture.Stop();

        Assert.Single(sink.Anchors);
        Assert.Equal(0, sink.Anchors[0].Frame);
        Assert.Equal([0L], sink.Blocks.Select(block => block.StartFrame));
        Assert.Equal(1_600, sink.Blocks[0].Microphone.Length);
        Assert.Equal(-0.5, sink.Blocks[0].System[0], 0.001);
    }

    [Fact]
    public void TestTwoSourcesLandOnTheirOwnChannels()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, system);
        capture.Start();
        for (int i = 0; i < 4; i++)
        {
            microphone.Deliver(Constant(0.5f, 800));
            system.Deliver(Constant(-0.5f, 800));
        }
        MeetingCapture.Summary summary = capture.Stop();

        Assert.Equal(3_200, summary.FrameCount);
        Assert.True(summary.SystemAudio.WasRequested);
        Assert.True(summary.SystemAudio.EverDeliveredBuffers);
        Assert.True(summary.SystemAudio.EverDeliveredAudio);
        var audio = ReadChannels(capture.AudioPath);
        Assert.Equal(0.5, audio.Left[1_000], 0.001);
        Assert.Equal(-0.5, audio.Right[1_000], 0.001);
    }

    [Fact]
    public void TestASilentSystemSideIsAliveButNotAudible()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, system);
        capture.Start();
        microphone.Deliver(Constant(0.3f, 1_600));
        system.Deliver(Constant(0, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.True(summary.SystemAudio.EverDeliveredBuffers, "buffers arrived - the monitor is alive");
        Assert.False(summary.SystemAudio.EverDeliveredAudio, "but nobody said anything");
        MeetingCapture.ChannelHealth health = capture.Health(MeetingChannel.System);
        Assert.NotNull(health.LastBlockAt);
        Assert.Null(health.LastAudibleAt);
    }

    [Fact]
    public void TestASilentMicrophoneIsAliveButNotAudible()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, system);
        capture.Start();
        microphone.Deliver(Constant(0, 1_600));
        system.Deliver(Constant(0.3f, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.True(summary.MicrophoneEverDeliveredBuffers, "buffers arrived - the engine is alive");
        Assert.False(summary.MicrophoneEverDeliveredAudio, "but the person was not in them");
        Assert.True(summary.SystemAudio.EverDeliveredAudio);
        MeetingCapture.ChannelHealth health = capture.Health(MeetingChannel.Microphone);
        Assert.NotNull(health.LastBlockAt);
        Assert.Null(health.LastAudibleAt);
        var audio = ReadChannels(capture.AudioPath);
        Assert.Equal(0, audio.Left[100], 0.001);
        Assert.Equal(0.3, audio.Right[100], 0.001);
    }

    [Fact]
    public void TestHostTimedBlocksFromTwoClocksStayAligned()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, system);
        capture.Start();
        ulong anchor = MeetingAudioBlock.HostNanosecondsNow();
        const ulong blockNanoseconds = 100_000_000;
        // The system side arrives 30 ms "late" by its own clock every block; that is jitter, and
        // the count must win over it.
        for (ulong i = 0; i < 5; i++)
        {
            ulong at = anchor + (i * blockNanoseconds);
            microphone.Deliver(Constant(0.5f, 1_600), at);
            system.Deliver(Constant(-0.5f, 1_600), at + 30_000_000);
        }
        MeetingCapture.Summary summary = capture.Stop();
        // The first block may land a few frames after zero (the anchor was taken just before), so
        // the file is 8 000 frames plus a little.
        Assert.True(summary.FrameCount >= 8_000);
        Assert.True(summary.FrameCount < 8_000 + 1_600);
        var audio = ReadChannels(capture.AudioPath);
        Assert.Equal(0.5, audio.Left[4_000], 0.001);
        Assert.Equal(-0.5, audio.Right[4_000], 0.001);
        // The system side's first block anchors 30 ms after the microphone's and stays there.
        int firstLeft = Array.FindIndex(audio.Left, sample => Math.Abs(sample) > 0.1);
        int firstRight = Array.FindIndex(audio.Right, sample => Math.Abs(sample) > 0.1);
        Assert.InRange(firstRight - firstLeft, 380, 580);
        Assert.Empty(summary.Gaps);
    }

    /// <summary>
    /// A reader stamps each block when its read returns. After a 300 ms stall the catch-up reads
    /// are stamped late and arrive back to back; the file must still hold every sample once, in
    /// order, with no silence between them.
    /// </summary>
    [Fact]
    public void TestAReaderStallFollowedByABurstLosesNoAudioAndInsertsNoGap()
    {
        var microphone = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        capture.Start();
        ulong anchor = MeetingAudioBlock.HostNanosecondsNow();
        const int chunk = 1_600;
        long now = 0;
        long read = 0;
        void Read(long at)
        {
            now = Math.Max(now, at);
            int count = (int)Math.Min(chunk, now - read);
            if (count <= 0) { return; }
            microphone.Deliver(Constant(0.5f, count), anchor + (ulong)((now - count) * 62_500));
            read += count;
        }
        for (int i = 1; i <= 20; i++) { Read(i * 320); }
        Read(now + 4_800 + 320);
        while (read < now) { Read(now + 2); }
        for (int i = 0; i < 40; i++) { Read(now + 320); }
        MeetingCapture.Summary summary = capture.Stop();

        var audio = ReadChannels(capture.AudioPath);
        int first = Array.FindIndex(audio.Left, sample => Math.Abs(sample) > 0.1);
        Assert.InRange(first, 0, 320);
        Assert.Equal(read, summary.FrameCount - first);
        Assert.All(audio.Left[first..], sample => Assert.Equal(0.5, sample, 0.001));
        Assert.Empty(summary.Gaps);
    }

    [Fact]
    public void TestAResumeWhoseSourceFailsKeepsThePauseOpen()
    {
        var microphone = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        capture.Start();
        microphone.Deliver(Constant(0.5f, 1_600));
        capture.Pause();
        microphone.StartError = new MeetingSourceFailure.StartFailed("gone");
        Assert.IsType<MeetingCapture.Failure.Microphone>(FailureOf(capture.Resume));
        Assert.Equal(MeetingCapture.CaptureState.Paused, capture.State);
        Thread.Sleep(100);
        MeetingCapture.Summary summary = capture.Stop();
        MeetingInterval pause = Assert.Single(summary.Pauses);
        Assert.True(pause.End - pause.Start >= 0.09, "the pause lasts until the stop, not the failed resume");
    }

    /// <summary>
    /// A recorder that keeps dying (the server is down) is restarted with growing waits, and after
    /// five tries in the window the side is reported down instead of respawned forever.
    /// </summary>
    [Fact]
    public async Task TestARestartStormBacksOffAndThenGivesUp()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var delays = new List<TimeSpan>();
        var failures = new List<MeetingCapture.Failure>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            system,
            onFailure: failure => { lock (failures) { failures.Add(failure); } })
        {
            RestartDelay = delay =>
            {
                lock (delays) { delays.Add(delay); }
                return Task.CompletedTask;
            },
        };
        capture.Start();
        for (int restart = 1; restart <= 5; restart++)
        {
            system.Fail(new MeetingSourceFailure.ConfigurationChanged());
            await WaitUntil(() => system.StartCount == 1 + restart);
        }
        system.Fail(new MeetingSourceFailure.ConfigurationChanged());
        await WaitUntil(() => { lock (failures) { return failures.Count > 0; } });
        await Task.Delay(100);
        Assert.Equal(6, system.StartCount);
        Assert.Equal(
            [0.5, 1, 2, 4, 8],
            delays.Select(delay => delay.TotalSeconds));
        Assert.IsType<MeetingCapture.Failure.SystemAudio>(Assert.Single(failures));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Contains(summary.Gaps, gap => gap.Channel == MeetingChannel.System);
    }

    [Fact]
    public void TestPauseStopsTheSourcesAndResumeStartsThemAgain()
    {
        var microphone = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        capture.Start();
        microphone.Deliver(Constant(0.5f, 1_600));
        capture.Pause();
        Assert.Equal(MeetingCapture.CaptureState.Paused, capture.State);
        Assert.Equal(1, microphone.StopCount);
        Assert.False(microphone.IsRunning, "nothing is captured while paused");
        Assert.Equal(1_600, capture.FrameCount);

        capture.Resume();
        Assert.Equal(2, microphone.StartCount);
        microphone.Deliver(Constant(0.5f, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Equal(3_200, summary.FrameCount);
        Assert.Single(summary.Pauses);
    }

    [Fact]
    public void TestStopAndPauseAreRefusedBeforeStartAndStartIsRefusedTwice()
    {
        var capture = new MeetingCapture(directory, new ScriptedAudioSource(), systemAudio: null);
        Assert.IsType<MeetingCapture.Failure.NotRecording>(FailureOf(() => capture.Stop()));
        Assert.IsType<MeetingCapture.Failure.NotRecording>(FailureOf(capture.Pause));
        capture.Start();
        Assert.IsType<MeetingCapture.Failure.NotIdle>(FailureOf(capture.Start));
        _ = capture.Stop();
    }

    [Fact]
    public async Task TestAMicrophoneConfigurationChangeRestartsItAndRecordsTheGap()
    {
        var microphone = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        capture.Start();
        microphone.Deliver(Constant(0.5f, 1_600));
        microphone.Fail(new MeetingSourceFailure.ConfigurationChanged());
        await WaitUntil(() => microphone.StartCount == 2);
        // Silence while the device was away, then audio again.
        microphone.Deliver(Constant(0.5f, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Equal(3_200, summary.FrameCount);
        MeetingGap gap = Assert.Single(summary.Gaps);
        Assert.Equal(MeetingChannel.Microphone, gap.Channel);
        Assert.Equal(MeetingGap.GapReason.MicrophoneUnavailable, gap.Reason);
    }

    /// <summary>
    /// Both sides recording; the microphone's device changes and its new run starts a second
    /// later. The new run's first block belongs where the host clock puts it: none of it is dropped
    /// as late, and the second it was away is silence on its own channel.
    /// </summary>
    [Fact]
    public async Task TestARestartedMicrophoneIsPlacedByHostTimeAndLosesNothing()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, system)
        {
            RestartDelay = _ => Task.CompletedTask,
        };
        capture.Start();
        ulong anchor = MeetingAudioBlock.HostNanosecondsNow();
        const int block = 1_600;
        const ulong blockNanoseconds = 100_000_000;
        ulong At(int i) => anchor + ((ulong)i * blockNanoseconds);
        for (int i = 0; i < 10; i++)
        {
            microphone.Deliver(Constant(0.5f, block), At(i));
            system.Deliver(Constant(-0.5f, block), At(i));
        }
        microphone.Fail(new MeetingSourceFailure.ConfigurationChanged());
        await WaitUntil(() => microphone.StartCount == 2);
        for (int i = 10; i < 20; i++) { system.Deliver(Constant(-0.5f, block), At(i)); }
        for (int i = 20; i < 30; i++)
        {
            microphone.Deliver(Constant(0.5f, block), At(i));
            system.Deliver(Constant(-0.5f, block), At(i));
        }
        MeetingCapture.Summary summary = capture.Stop();

        var audio = ReadChannels(capture.AudioPath);
        int first = Array.FindIndex(audio.Left, sample => Math.Abs(sample) > 0.1);
        Assert.InRange(first, 0, 320);
        Assert.Equal(20 * block, audio.Left.Count(sample => Math.Abs(sample) > 0.1));
        Assert.All(audio.Left[(first + (11 * block))..(first + (19 * block))], sample => Assert.Equal(0, sample, 0.001));
        Assert.All(audio.Left[(first + (20 * block))..(first + (30 * block))], sample => Assert.Equal(0.5, sample, 0.001));
        MeetingGap gap = Assert.Single(summary.Gaps);
        Assert.Equal(MeetingChannel.Microphone, gap.Channel);
        Assert.True(gap.Start <= 1.0 && gap.End >= 1.5, $"gap {gap.Start}-{gap.End}");
    }

    /// <summary>
    /// A failure queued by a run that was then stopped and started again - a quick pause and
    /// resume - belongs to the old run and must not stop the healthy new one.
    /// </summary>
    [Fact]
    public async Task TestAFailureFromAPreviousRunDoesNotStopTheNewOne()
    {
        var microphone = new ScriptedAudioSource();
        var failures = new List<MeetingCapture.Failure>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            systemAudio: null,
            onFailure: failure => { lock (failures) { failures.Add(failure); } })
        {
            RestartDelay = _ => Task.CompletedTask,
        };
        capture.Start();
        capture.Pause();
        capture.Resume();
        Assert.Equal(2, microphone.StartCount);
        microphone.FailRun(1, new MeetingSourceFailure.ConfigurationChanged());
        microphone.FailRun(1, new MeetingSourceFailure.StartFailed("stale"));
        await Task.Delay(200);
        Assert.Equal(1, microphone.StopCount);
        Assert.Equal(2, microphone.StartCount);
        Assert.True(microphone.IsRunning);
        lock (failures) { Assert.Empty(failures); }
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Empty(summary.Gaps);
    }

    [Fact]
    public void TestRefusesToStartWhenTheDiskIsNearlyFull()
    {
        var capture = new MeetingCapture(
            directory,
            new ScriptedAudioSource(),
            systemAudio: null,
            freeBytes: _ => 100L * 1_024 * 1_024);
        Assert.Equal(new MeetingCapture.Failure.DiskFull(), FailureOf(capture.Start));
    }

    [Fact]
    public async Task TestTheDiskFillingMidRecordingStopsItCleanlyWithTheReasonKept()
    {
        var microphone = new ScriptedAudioSource();
        long free = 10L * 1_024 * 1_024 * 1_024;
        MeetingCapture.Failure? reported = null;
        var capture = new MeetingCapture(
            directory,
            microphone,
            systemAudio: null,
            onFailure: failure => Volatile.Write(ref reported, failure),
            freeBytes: _ => Interlocked.Read(ref free));
        capture.Start();
        Interlocked.Exchange(ref free, 50L * 1_024 * 1_024);
        // The disk is looked at once a minute of audio; deliver past that plus the jitter window
        // the aligner holds back.
        for (int i = 0; i < 610; i++) { microphone.Deliver(Constant(0.1f, 1_600)); }
        await WaitUntil(
            () => Volatile.Read(ref reported) is MeetingCapture.Failure.DiskFull,
            TimeSpan.FromSeconds(10));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Equal(MeetingEndReason.DiskFull, summary.EndReason);
        Assert.True(summary.FrameCount > 0, "what reached disk before the stop is kept");
        var audio = ReadChannels(capture.AudioPath);
        Assert.Equal(summary.FrameCount, audio.Left.Length);
    }

    [Fact]
    public void TestLevelsArePublishedForTheMeters()
    {
        var microphone = new ScriptedAudioSource();
        var seen = new List<MeetingCapture.Levels>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            systemAudio: null,
            onLevels: levels => { lock (seen) { seen.Add(levels); } });
        capture.Start();
        microphone.Deliver(Constant(0.7f, 1_600));
        microphone.Deliver(Constant(0.1f, 1_600));
        _ = capture.Stop();
        Assert.Equal(2, seen.Count);
        Assert.Equal(0.7f, seen[0].Microphone);
        Assert.Equal(0f, seen[0].System);
        Assert.Equal(0.1f, capture.CurrentLevels.Microphone);
    }

    /// <summary>
    /// Segments name frames that are on disk, per channel, cut inside silence - and the tail at
    /// stop comes out too.
    /// </summary>
    [Fact]
    public void TestSpeechIsHandedOutAsSegmentsThatIndexTheFile()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var segments = new List<MeetingSegmentRef>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            system,
            segmentParameters: new MeetingSegmentPolicy.Parameters(
                speech: new SpeechSegmenter.Parameters(minimumSegment: TimeSpan.FromSeconds(2)),
                hardCap: TimeSpan.FromSeconds(20)),
            onSegment: segment => { lock (segments) { segments.Add(segment); } });
        capture.Start();
        // Three seconds of speech on the microphone, a one-second pause, two more seconds; the
        // other side stays silent throughout.
        for (int i = 0; i < 30; i++) { microphone.Deliver(Constant(0.5f, 1_600)); system.Deliver(Constant(0, 1_600)); }
        for (int i = 0; i < 10; i++) { microphone.Deliver(Constant(0, 1_600)); system.Deliver(Constant(0, 1_600)); }
        for (int i = 0; i < 20; i++) { microphone.Deliver(Constant(0.5f, 1_600)); system.Deliver(Constant(0, 1_600)); }
        MeetingCapture.Summary summary = capture.Stop();

        Assert.Equal([MeetingChannel.Microphone, MeetingChannel.Microphone], segments.Select(s => s.Channel));
        Assert.Equal(0, segments[0].StartFrame);
        Assert.True(segments[0].FrameCount > 48_000, "the cut is inside the pause, after the speech");
        Assert.True(segments[0].FrameCount < 64_000);
        Assert.Equal(segments[0].EndFrame, segments[1].StartFrame);
        Assert.Equal(summary.FrameCount, segments[1].EndFrame);

        // And the frames a segment names are readable from the file, as the audio that was
        // delivered.
        var reader = new MeetingPcmWindowReader(capture.AudioPath);
        float[] tail = reader.Read(segments[1]);
        Assert.Equal(0.5, tail.Length == 0 ? 0 : tail[^1], 0.001);
    }

    [Fact]
    public void TestAPauseFlushesTheTailAsASegment()
    {
        var microphone = new ScriptedAudioSource();
        var segments = new List<MeetingSegmentRef>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            systemAudio: null,
            onSegment: segment => { lock (segments) { segments.Add(segment); } });
        capture.Start();
        for (int i = 0; i < 20; i++) { microphone.Deliver(Constant(0.5f, 1_600)); }
        capture.Pause();
        Assert.Single(segments);
        Assert.Equal(32_000, segments[0].FrameCount);
        capture.Resume();
        for (int i = 0; i < 5; i++) { microphone.Deliver(Constant(0, 1_600)); }
        _ = capture.Stop();
        Assert.Single(segments);
    }

    /// <summary>
    /// The Swift test also counts probe tones (two: start and the new route); the probe is not
    /// ported on Linux.
    /// </summary>
    [Fact]
    public async Task TestAnOutputRouteChangeRestartsTheTapProbesAgainAndRecordsTheGap()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, system);
        capture.Start();
        microphone.Deliver(Constant(0.5f, 1_600));
        system.Deliver(Constant(-0.5f, 1_600));
        system.Fail(new MeetingSourceFailure.ConfigurationChanged());
        await WaitUntil(() => system.StartCount == 2);
        microphone.Deliver(Constant(0.5f, 1_600));
        system.Deliver(Constant(-0.5f, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Equal([MeetingGap.GapReason.SystemAudioRouteChange], summary.Gaps.Select(g => g.Reason));
        Assert.Equal(MeetingChannel.System, summary.Gaps[0].Channel);
        Assert.Equal("Scripted", summary.SystemAudio.OutputTransport);
    }

    /// <summary>A monitor that will not start costs the other side, never the recording.</summary>
    [Fact]
    public void TestATapThatCannotStartLeavesTheMicrophoneRecordingAndSaysSo()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource
        {
            StartError = new MeetingSourceFailure.StartFailed("the audio tap could not be created (-1)"),
        };
        var failures = new List<MeetingCapture.Failure>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            system,
            onFailure: failure => { lock (failures) { failures.Add(failure); } });
        capture.Start();
        Assert.Equal(MeetingCapture.CaptureState.Recording, capture.State);
        Assert.Equal(
            [new MeetingCapture.Failure.SystemAudio(
                new MeetingSourceFailure.StartFailed("the audio tap could not be created (-1)"))],
            failures);
        microphone.Deliver(Constant(0.5f, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Equal(1_600, summary.FrameCount);
        Assert.True(summary.SystemAudio.WasRequested);
        Assert.False(summary.SystemAudio.EverDeliveredBuffers);
        Assert.Equal([MeetingChannel.System], summary.Gaps.Select(g => g.Channel));
    }

    [Fact]
    public void TestAMicrophoneThatCannotStartOnAMeetingLeavesTheOtherSideRecording()
    {
        var microphone = new ScriptedAudioSource
        {
            StartError = new MeetingSourceFailure.StartFailed("another app has exclusive access to the microphone"),
        };
        var system = new ScriptedAudioSource();
        var failures = new List<MeetingCapture.Failure>();
        var capture = new MeetingCapture(
            Path.Combine(directory, "mic-down"),
            microphone,
            system,
            onFailure: failure => { lock (failures) { failures.Add(failure); } });
        capture.Start();
        Assert.Equal(MeetingCapture.CaptureState.Recording, capture.State);
        Assert.Equal(
            [new MeetingCapture.Failure.Microphone(
                new MeetingSourceFailure.StartFailed("another app has exclusive access to the microphone"))],
            failures);
        system.Deliver(Constant(0.5f, 1_600));
        MeetingCapture.Summary summary = capture.Stop();
        Assert.Equal(1_600, summary.FrameCount);
        Assert.False(summary.MicrophoneEverDeliveredAudio);
        Assert.True(summary.SystemAudio.EverDeliveredAudio);
        Assert.Equal([MeetingChannel.Microphone], summary.Gaps.Select(g => g.Channel));
    }

    [Fact]
    public void TestAMicrophoneThatCannotStartOnAVoiceNoteRefusesToStart()
    {
        var microphone = new ScriptedAudioSource { StartError = new MeetingSourceFailure.StartFailed("unavailable") };
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        Assert.Equal(
            new MeetingCapture.Failure.Microphone(new MeetingSourceFailure.StartFailed("unavailable")),
            FailureOf(capture.Start));
    }

    [Fact]
    public void TestFailedStartClosesTheSourcesAndFinishesTheWriter()
    {
        var microphone = new ScriptedAudioSource { StartError = new MeetingSourceFailure.StartFailed("unavailable") };
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);

        _ = FailureOf(capture.Start);

        Assert.Equal(MeetingCapture.CaptureState.Idle, capture.State);
        Assert.True(microphone.StopCount > 0, "even a partly started source is closed");
        Assert.Equal(0, WavFile.Read(capture.AudioPath).Length);
    }

    [Fact]
    public void TestRecoveringTheMicrophoneRestartsItOnceOnTheDefaultDevice()
    {
        var microphone = new ScriptedAudioSource();
        var capture = new MeetingCapture(directory, microphone, systemAudio: null);
        capture.Start();
        microphone.Deliver(Constant(0, 1_600));
        capture.RecoverMicrophone();
        Assert.Equal(1, microphone.RecoveryCount);
        Assert.Equal(2, microphone.StartCount);
        Assert.Equal(1, microphone.StopCount);
        microphone.Deliver(Constant(0.5f, 1_600));
        capture.RecoverMicrophone();
        Assert.Equal(1, microphone.RecoveryCount);
        Assert.Equal(2, microphone.StartCount);
        MeetingCapture.Summary summary = capture.Stop();
        Assert.True(summary.MicrophoneEverDeliveredAudio);
        Assert.Equal([MeetingGap.GapReason.MicrophoneUnavailable], summary.Gaps.Select(g => g.Reason));
    }

    /// <summary>Three seconds of pseudo-random "speech" and its leak into the microphone, 30 ms late at half level.</summary>
    private static (float[] Speech, float[] Echo) SpeechAndEcho()
    {
        ulong state = 42;
        float[] speech = new float[48_000];
        for (int i = 0; i < speech.Length; i++)
        {
            state = unchecked((state * 6_364_136_223_846_793_005) + 1_442_695_040_888_963_407);
            speech[i] = (((float)(state >> 40) / (1 << 24) * 2) - 1) * 0.3f;
        }
        float[] echo = new float[48_000];
        for (int i = 480; i < echo.Length; i++) { echo[i] = speech[i - 480] * 0.5f; }
        return (speech, echo);
    }

    /// <summary>
    /// The speakers leaking into the microphone are not the person speaking: the other side's
    /// sentence is one segment on its own channel, not two.
    /// </summary>
    [Fact]
    public void TestTheOtherSideLeakingIntoTheMicrophoneIsNotSegmentedAsYou()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var segments = new List<MeetingSegmentRef>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            system,
            segmentParameters: new MeetingSegmentPolicy.Parameters(
                speech: new SpeechSegmenter.Parameters(minimumSegment: TimeSpan.FromSeconds(2))),
            onSegment: segment => { lock (segments) { segments.Add(segment); } });
        capture.Start();
        (float[] speech, float[] echo) = SpeechAndEcho();
        for (int block = 0; block < 30; block++)
        {
            microphone.Deliver(echo[(block * 1_600)..((block + 1) * 1_600)]);
            system.Deliver(speech[(block * 1_600)..((block + 1) * 1_600)]);
        }
        for (int i = 0; i < 10; i++)
        {
            microphone.Deliver(Constant(0, 1_600));
            system.Deliver(Constant(0, 1_600));
        }
        _ = capture.Stop();
        Assert.Equal([MeetingChannel.System], segments.Select(s => s.Channel));
    }

    /// <summary>
    /// Every remote sentence starts after silence, and the gate can only match once some of it
    /// has been heard: the frames before that must still be told silence, or the sentence comes
    /// back as a paragraph.
    /// </summary>
    [Fact]
    public void TestAnEchoThatStartsAfterSilenceIsNotSegmentedAsYouEither()
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        var segments = new List<MeetingSegmentRef>();
        var capture = new MeetingCapture(
            directory,
            microphone,
            system,
            segmentParameters: new MeetingSegmentPolicy.Parameters(
                speech: new SpeechSegmenter.Parameters(minimumSegment: TimeSpan.FromSeconds(2))),
            onSegment: segment => { lock (segments) { segments.Add(segment); } });
        capture.Start();
        (float[] speech, float[] echo) = SpeechAndEcho();
        // A second of nothing first, then the leak in the small blocks real sources deliver: the
        // first of them arrive before there is anything to match against, and used to slip
        // through.
        for (int i = 0; i < 10; i++)
        {
            microphone.Deliver(Constant(0, 1_600));
            system.Deliver(Constant(0, 1_600));
        }
        for (int start = 0; start < 48_000; start += 512)
        {
            int end = Math.Min(start + 512, 48_000);
            microphone.Deliver(echo[start..end]);
            system.Deliver(speech[start..end]);
        }
        for (int i = 0; i < 10; i++)
        {
            microphone.Deliver(Constant(0, 1_600));
            system.Deliver(Constant(0, 1_600));
        }
        _ = capture.Stop();
        Assert.Equal([MeetingChannel.System], segments.Select(s => s.Channel));
    }

    /// <summary>
    /// Segments are raised with no capture lock held: a consumer that pauses from inside the
    /// callback - directly, or by handing the pause to another thread and waiting, as a UI
    /// dispatcher would - neither deadlocks nor re-enters the callback.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestASegmentHandlerCanPauseTheCapture(bool fromAnotherThread)
    {
        var microphone = new ScriptedAudioSource();
        var segments = new List<MeetingSegmentRef>();
        int depth = 0;
        bool reentered = false;
        MeetingCapture? capture = null;
        capture = new MeetingCapture(
            directory,
            microphone,
            systemAudio: null,
            segmentParameters: new MeetingSegmentPolicy.Parameters(
                speech: new SpeechSegmenter.Parameters(minimumSegment: TimeSpan.FromSeconds(2)),
                hardCap: TimeSpan.FromSeconds(20)),
            onSegment: segment =>
            {
                reentered |= Interlocked.Increment(ref depth) > 1;
                bool first;
                lock (segments) { segments.Add(segment); first = segments.Count == 1; }
                if (first)
                {
                    if (fromAnotherThread)
                    {
                        Assert.True(Task.Run(capture!.Pause).Wait(TimeSpan.FromSeconds(5)), "the pause deadlocked");
                    }
                    else
                    {
                        capture!.Pause();
                    }
                }
                Interlocked.Decrement(ref depth);
            });
        capture.Start();

        // Delivered from a source thread, as a real device would.
        await Task.Run(() =>
        {
            for (int i = 0; i < 30; i++) { microphone.Deliver(Constant(0.5f, 1_600)); }
            for (int i = 0; i < 10; i++) { microphone.Deliver(Constant(0, 1_600)); }
            for (int i = 0; i < 20; i++) { microphone.Deliver(Constant(0.5f, 1_600)); }
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MeetingCapture.CaptureState.Paused, capture.State);
        Assert.False(reentered, "the handler was re-entered");
        _ = capture.Stop();
        lock (segments)
        {
            Assert.NotEmpty(segments);
            for (int i = 1; i < segments.Count; i++) { Assert.Equal(segments[i - 1].EndFrame, segments[i].StartFrame); }
        }
    }

    /// <summary>
    /// Failures are raised with no capture lock held: a handler that stops the recording -
    /// directly, or by handing the stop to another thread and waiting - does not deadlock.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestAFailureHandlerCanStopTheCapture(bool fromAnotherThread)
    {
        var microphone = new ScriptedAudioSource();
        var system = new ScriptedAudioSource();
        MeetingCapture.Summary? stopped = null;
        MeetingCapture? capture = null;
        capture = new MeetingCapture(
            directory,
            microphone,
            system,
            onFailure: _ =>
            {
                if (fromAnotherThread)
                {
                    var stop = Task.Run(capture!.Stop);
                    Assert.True(stop.Wait(TimeSpan.FromSeconds(5)), "the stop deadlocked");
                    Volatile.Write(ref stopped, stop.Result);
                }
                else
                {
                    Volatile.Write(ref stopped, capture!.Stop());
                }
            });
        capture.Start();
        microphone.Deliver(Constant(0.5f, 1_600));
        system.Fail(new MeetingSourceFailure.StartFailed("gone"));
        await WaitUntil(() => Volatile.Read(ref stopped) is not null, TimeSpan.FromSeconds(10));
        Assert.Equal(MeetingCapture.CaptureState.Stopped, capture.State);
    }
}
