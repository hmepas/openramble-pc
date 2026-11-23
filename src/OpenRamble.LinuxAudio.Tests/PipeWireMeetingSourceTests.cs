using System.Collections.Concurrent;
using System.Diagnostics;
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
        var source = Source(recorder, clock: () => Stopwatch.Frequency); // one second

        source.Start(blocks.Add, _ => { });
        recorder.Feed(new float[320]); // 20 ms
        Assert.True(blocks.TryTake(out MeetingAudioBlock? block, Wait));
        source.Stop();

        Assert.Equal(980_000_000UL, block!.HostNanoseconds);
        Assert.Equal(320, block.Samples.Length);
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
    public void AMissingRecorderIsUnavailable()
    {
        var failures = new List<MeetingSourceFailure>();
        var source = new PipeWireMeetingSource(
            PipeWireMeetingSource.SourceKind.SystemAudio, null, null,
            _ => throw new AudioCaptureError.EngineUnavailable("pw-record was not found on PATH."));

        source.Start(_ => Assert.Fail("no audio expected"), failures.Add);
        source.Stop();

        MeetingSourceFailure.Unavailable unavailable =
            Assert.IsType<MeetingSourceFailure.Unavailable>(Assert.Single(failures));
        Assert.Contains("pw-record", unavailable.Detail, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void TheRealSystemSourceWithoutPwRecordIsUnavailable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "/nonexistent/openramble-has-no-recorder");
            var failures = new List<MeetingSourceFailure>();

            PipeWireMeetingSource.SystemAudio().Start(_ => { }, failures.Add);

            Assert.IsType<MeetingSourceFailure.Unavailable>(Assert.Single(failures));
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

    private static PipeWireMeetingSource Source(FakeRecorderProcess recorder, Func<long>? clock = null) =>
        new(PipeWireMeetingSource.SourceKind.SystemAudio, null, null, _ => recorder, clock);
}
