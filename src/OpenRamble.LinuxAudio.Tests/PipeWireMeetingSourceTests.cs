using System.Collections.Concurrent;
using DictationCore;
using DictationCore.Audio;

namespace OpenRamble.LinuxAudio.Tests;

[Collection(LinuxCaptureCollection.Name)]
public sealed class PipeWireMeetingSourceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void SystemAudioRecordsTheDefaultSinkMonitorWithNoRoleAndNoTarget()
    {
        string[] arguments =
            [.. PwRecordProcess.Arguments(new AudioStreamFormat(16_000, 1), "ignored", captureSink: true)];

        Assert.Equal(
            [
                "--rate", "16000",
                "--channels", "1",
                "--format", "f32",
                "--raw",
                "--latency", "20ms",
                "-P", "stream.capture.sink=true",
                "-P", "node.name=" + PwRecordProcess.SystemAudioNodeName,
                "-P", "media.name=\"OpenRamble meeting audio\"",
                "-",
            ],
            arguments);
        Assert.StartsWith("openramble-meeting-system-", PwRecordProcess.SystemAudioNodeName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMicrophoneHonoursTheChosenInputAndSystemAudioChoosesNothing()
    {
        var targets = new List<string?>();
        var microphone = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.Microphone, "alsa_input.usb", "USB Mic",
            chosen => { targets.Add(chosen); return new FakeRecorderProcess(); });
        var system = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.SystemAudio, "alsa_input.usb", null,
            chosen => { targets.Add(chosen); return new FakeRecorderProcess(); });

        microphone.Start(_ => { }, _ => { });
        system.Start(_ => { }, _ => { });
        microphone.Stop();
        system.Stop();

        Assert.Equal(["alsa_input.usb", null], targets);
        Assert.Equal("USB Mic", microphone.DeviceName);
    }

    [Fact]
    public void ABlockIsStampedAtItsFirstFrame()
    {
        var recorder = new FakeRecorderProcess();
        var blocks = new BlockingCollection<MeetingAudioBlock>();
        var source = Source(recorder, clock: () => 1_000_000_000UL); // one second

        source.Start(blocks.Add, _ => { });
        recorder.Feed(new float[320]); // 20 ms
        Assert.True(blocks.TryTake(out MeetingAudioBlock? block, Wait));
        source.Stop();

        Assert.Equal(980_000_000UL, block!.HostNanoseconds);
        Assert.Equal(320, block.Samples.Length);
    }

    /// <summary>
    /// A consumer that takes its time must not hold up the read: the blocks behind it are still
    /// read, and stamped, when they arrive - then handed over in order once it returns.
    /// </summary>
    [Fact]
    public void ASlowConsumerDoesNotStallTheReader()
    {
        var recorder = new FakeRecorderProcess();
        var blocks = new BlockingCollection<MeetingAudioBlock>();
        using var release = new ManualResetEventSlim();
        int stamped = 0;
        var source = Source(recorder, clock: () => (ulong)Interlocked.Increment(ref stamped) * 1_000_000_000UL);

        source.Start(
            block =>
            {
                release.Wait(Wait);
                blocks.Add(block);
            },
            _ => { });
        recorder.Feed(new float[320]);
        SpinWait.SpinUntil(() => Volatile.Read(ref stamped) == 1, Wait);
        recorder.Feed(new float[320]);
        recorder.Feed(new float[320]);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref stamped) == 3, Wait), "the reader kept reading");
        Assert.Empty(blocks);
        release.Set();
        var stamps = new List<ulong>();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(blocks.TryTake(out MeetingAudioBlock? block, Wait));
            stamps.Add(block!.HostNanoseconds!.Value);
        }
        source.Stop();

        Assert.Equal([980_000_000UL, 1_980_000_000UL, 2_980_000_000UL], stamps);
    }

    /// <summary>
    /// A stop that gave up waiting for a slow consumer has returned; the blocks still queued must
    /// not be delivered after it - after a pause and resume they would land at the resume point.
    /// </summary>
    [Fact]
    public void AStopThatGivesUpOnASlowConsumerDeliversNothingAfterIt()
    {
        var recorder = new FakeRecorderProcess();
        var blocks = new BlockingCollection<MeetingAudioBlock>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int stamped = 0;
        var source = Source(recorder, clock: () => (ulong)Interlocked.Increment(ref stamped) * 1_000_000_000UL);

        source.Start(
            block =>
            {
                entered.Set();
                release.Wait(Wait);
                blocks.Add(block);
            },
            _ => { });
        recorder.Feed(new float[320]);
        Assert.True(entered.Wait(Wait));
        recorder.Feed(new float[320]);
        recorder.Feed(new float[320]);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref stamped) == 3, Wait), "the reader kept reading");

        source.Stop();
        release.Set();

        Assert.True(blocks.TryTake(out _, Wait), "the block in hand finishes");
        Assert.False(blocks.TryTake(out _, TimeSpan.FromMilliseconds(300)), "nothing queued is delivered after the stop");
    }

    [Fact]
    public void ARecorderThatExitsOnItsOwnIsAChangedDevice()
    {
        var recorder = new FakeRecorderProcess();
        var failures = new BlockingCollection<MeetingSourceFailure>();
        var source = Source(recorder);

        source.Start(_ => { }, failures.Add);
        recorder.Die();

        Assert.True(failures.TryTake(out MeetingSourceFailure? failure, Wait));
        Assert.IsType<MeetingSourceFailure.ConfigurationChanged>(failure);
        Assert.False(failures.TryTake(out _, TimeSpan.FromMilliseconds(200)));
        source.Stop();
    }

    [Fact]
    public void NonFiniteSamplesAreAConversionFailure()
    {
        var recorder = new FakeRecorderProcess();
        var failures = new BlockingCollection<MeetingSourceFailure>();
        var source = Source(recorder);

        source.Start(_ => { }, failures.Add);
        recorder.Feed(0.1f, float.NaN);

        Assert.True(failures.TryTake(out MeetingSourceFailure? failure, Wait));
        Assert.IsType<MeetingSourceFailure.ConversionFailed>(failure);
        source.Stop();
    }

    [Fact]
    public void AMissingRecorderIsUnavailableAndThrownFromStart()
    {
        var source = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.SystemAudio, null, null,
            _ => throw new AudioCaptureError.EngineUnavailable("pw-record was not found on PATH."));

        MeetingSourceException error = Assert.Throws<MeetingSourceException>(
            () => source.Start(_ => Assert.Fail("no audio expected"), _ => Assert.Fail("no failure callback expected")));
        source.Stop();

        MeetingSourceFailure.Unavailable unavailable =
            Assert.IsType<MeetingSourceFailure.Unavailable>(error.Failure);
        Assert.Contains("pw-record", unavailable.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ALauncherThatReportsAStartFailureIsThrownAsIs()
    {
        var source = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.Microphone, null, null,
            _ => throw new MeetingSourceException(new MeetingSourceFailure.StartFailed("refused")));

        MeetingSourceException error = Assert.Throws<MeetingSourceException>(
            () => source.Start(_ => { }, _ => Assert.Fail("no failure callback expected")));

        Assert.Equal(new MeetingSourceFailure.StartFailed("refused"), error.Failure);
    }

    [Fact]
    public void ARealBlockIsStampedOnTheCapturesHostClock()
    {
        var recorder = new FakeRecorderProcess();
        var blocks = new BlockingCollection<MeetingAudioBlock>();
        var source = Source(recorder);

        ulong before = MeetingAudioBlock.HostNanosecondsNow();
        source.Start(blocks.Add, _ => { });
        recorder.Feed(new float[320]); // 20 ms
        Assert.True(blocks.TryTake(out MeetingAudioBlock? block, Wait));
        ulong after = MeetingAudioBlock.HostNanosecondsNow();
        source.Stop();

        Assert.InRange(block!.HostNanoseconds!.Value, before - 20_000_000UL, after - 20_000_000UL);
    }

    [LinuxFact]
    public void TheRealSystemSourceWithoutPwRecordIsUnavailable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "/nonexistent/openramble-has-no-recorder");
            MeetingSourceException error = Assert.Throws<MeetingSourceException>(
                () => PipeWireMeetingSource.SystemAudio().Start(_ => { }, _ => { }));

            Assert.IsType<MeetingSourceFailure.Unavailable>(error.Failure);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    [Fact]
    public void StopIsIdempotentAndStartWorksAgainAfterIt()
    {
        var recorders = new List<FakeRecorderProcess>();
        var blocks = new BlockingCollection<MeetingAudioBlock>();
        var failures = new List<MeetingSourceFailure>();
        var source = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.Microphone, null, null,
            _ =>
            {
                var recorder = new FakeRecorderProcess();
                recorders.Add(recorder);
                return recorder;
            });

        source.Start(blocks.Add, failures.Add);
        source.Stop();
        source.Stop();
        source.Start(blocks.Add, failures.Add);
        recorders[1].Feed(0.25f);
        Assert.True(blocks.TryTake(out MeetingAudioBlock? block, Wait));
        source.Stop();
        source.Stop();

        Assert.Equal([0.25f], block!.Samples);
        Assert.Equal(2, recorders.Count);
        Assert.All(recorders, recorder => Assert.True(recorder.Disposed));
        Assert.Equal(1, recorders[0].StopRequests);
        Assert.Empty(failures);
    }

    [Fact]
    public void StartingTwiceWithoutAStopIsRefused()
    {
        var source = Source(new FakeRecorderProcess());
        source.Start(_ => { }, _ => { });

        Assert.Throws<InvalidOperationException>(() => source.Start(_ => { }, _ => { }));
        source.Stop();
    }

    [Fact]
    public void RecoveryDropsTheChosenMicrophone()
    {
        var targets = new List<string?>();
        var source = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.Microphone, "alsa_input.usb", "USB Mic",
            chosen => { targets.Add(chosen); return new FakeRecorderProcess(); });

        source.PrepareForRecovery();
        source.Start(_ => { }, _ => { });
        source.Stop();

        Assert.Equal([null], targets);
        Assert.Null(source.DeviceName);
    }

    private static PipeWireMeetingSource Source(FakeRecorderProcess recorder, Func<ulong>? clock = null) =>
        new(PipeWireMeetingSource.SourceKind.SystemAudio, null, null, _ => recorder, clock);
}
