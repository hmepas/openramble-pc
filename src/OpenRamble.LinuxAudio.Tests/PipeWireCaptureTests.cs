using System.Diagnostics;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxAudio.Tests;

/// <summary>
/// The capture with the recorder replaced by one that misbehaves on request.
/// </summary>
/// <remarks>
/// The Windows peer has no tests: WASAPI cannot be driven without a device, and a fake device
/// would be NAudio's contract restated rather than the capture's. The recorder here is a process
/// behind five methods, which puts the three things that actually decide whether a dictation
/// survives inside reach of a test: the framing, the failure paths, and the stop that has to
/// return whatever the audio stack is doing.
/// </remarks>
[Collection(LinuxCaptureCollection.Name)]
public sealed class PipeWireCaptureTests : IDisposable
{
    /// <summary>Long enough that a slow runner is not a failure, short enough to fail a hang.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string takes = Path.Combine(
        Path.GetTempPath(),
        "openramble-linuxaudio-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(takes)) { Directory.Delete(takes, recursive: true); }
        }
        catch (IOException)
        {
            // A take the test left open. The directory is under the system temp folder.
        }
    }

    [Fact]
    public async Task TheTakeHoldsExactlyWhatTheRecorderProduced()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);

        string path = await capture.StartRecording();
        Assert.True(File.Exists(path));

        float[] spoken = [.. Enumerable.Range(0, 4_000).Select(index => index / 4_000f - 0.5f)];
        recorder.Feed(spoken);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));

        (string url, TimeSpan duration) = await capture.StopRecording();

        Assert.Equal(4_000L, capture.DeliveredCounts.Samples);
        Assert.True(capture.DeliveredCounts.Frames > 0);
        Assert.Equal(TimeSpan.FromSeconds(4_000 / 16_000.0), duration);
        Assert.True(File.Exists(url));

        // The in-memory copy recognition reads: the same samples, in the same order, undamaged by
        // the fact that they arrived in pieces a pipe chose.
        Assert.Equal(spoken, (await capture.TakeBufferedSamples())!);
        Assert.NotNull(await capture.StartupLatency());
    }

    [Fact]
    public async Task TheLevelIsAPeakAndNeverTheSamples()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);
        var levels = new List<float>();
        capture.OnInputLevel = level => levels.Add(level);

        await capture.StartRecording();
        recorder.Feed(0.1f, -0.75f, 0.2f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        await capture.StopRecording();

        Assert.NotEmpty(levels);
        Assert.Equal(0.75f, levels.Max());
    }

    [Fact]
    public async Task StartingSaysWhatItStartedAndInWhatFormat()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);

        await capture.StartRecording();
        await capture.StopRecording();

        Assert.Equal("true", diagnostics.Field("capture.start", "ok"));
        Assert.Equal("16000", diagnostics.Field("capture.start", "device_rate"));
        Assert.Equal("1", diagnostics.Field("capture.start", "device_channels"));
        // Nothing in this process resamples: the recorder is asked for the recording format and
        // PipeWire produces it. The event means the same thing it means on Windows.
        Assert.Equal("false", diagnostics.Field("capture.start", "resampled"));
        Assert.NotNull(diagnostics.Event("capture.stop.begin"));
        Assert.NotNull(diagnostics.Event("capture.stop.end"));
    }

    [Fact]
    public async Task TheFirstFrameIsAnnouncedOnceItIsReal()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);

        await capture.StartRecording();
        Assert.Null(diagnostics.Event("capture.first-frame"));

        recorder.Feed(0.5f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        await capture.StopRecording();

        Assert.NotNull(diagnostics.Event("capture.first-frame"));
        Assert.Equal(1, diagnostics.Lines.Count(line => line.Contains("capture.first-frame", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARecorderThatCannotBeStartedIsAFailedStartAndNoTake()
    {
        using var diagnostics = new DiagnosticRecorder();
        using var capture = new PipeWireCapture(
            takes,
            () => throw new AudioCaptureError.EngineUnavailable(
                "pw-record could not be started (No such file or directory)"));

        AudioCaptureError.EngineUnavailable error =
            await Assert.ThrowsAsync<AudioCaptureError.EngineUnavailable>(capture.StartRecording);

        Assert.Contains("pw-record", error.Message, StringComparison.Ordinal);
        Assert.Equal("false", diagnostics.Field("capture.start", "ok"));
        // The take was opened before the recorder was launched and has to go with it: a zero-byte
        // WAV left in the takes folder is a recording of a dictation that never happened.
        Assert.Empty(Directory.EnumerateFiles(takes));

        // And the capture is free again - a failed start must not leave a session behind.
        await Assert.ThrowsAsync<AudioCaptureError.NotRecording>(capture.StopRecording);
    }

    [Fact]
    public async Task ARecorderThatDiesMidDictationSaysSoInItsOwnWords()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess
        {
            ExitReport = "pw-record exited with status 1: error: pw_context_connect() failed: Host is down",
        };
        using var capture = new PipeWireCapture(takes, () => recorder);
        AudioCaptureError? reported = null;
        capture.Failed = error => reported = error;

        await capture.StartRecording();
        recorder.Die();

        // False, not an exception and not a wait that never ends: whoever was about to be told
        // "speak" is told there is no recording instead.
        Assert.False(await capture.WaitForFirstFrame().WaitAsync(Patience));
        Assert.IsType<AudioCaptureError.EngineUnavailable>(reported);
        Assert.Contains("Host is down", reported!.Message, StringComparison.Ordinal);
        Assert.Equal("EngineUnavailable", diagnostics.Field("capture.failed", "kind"));

        await capture.StopRecording();
    }

    [Fact]
    public async Task OneDeathRaisesOneAlarm()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);
        int alarms = 0;
        capture.Failed = _ => alarms += 1;

        await capture.StartRecording();
        recorder.Die();
        Assert.False(await capture.WaitForFirstFrame().WaitAsync(Patience));

        // The stop that follows finds a recorder that is already gone, which is not a second
        // failure - it is the same one, and the person must not be told twice.
        await capture.StopRecording();

        Assert.Equal(1, alarms);
    }

    [Fact]
    public async Task AnOrdinaryStopIsNotAFailure()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);
        AudioCaptureError? reported = null;
        capture.Failed = error => reported = error;

        await capture.StartRecording();
        recorder.Feed(0.5f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        await capture.StopRecording();

        // The stream ends at every stop. Reporting that would end every dictation with an error
        // nobody caused, which is why the session knows it is finishing before the signal is sent.
        Assert.Null(reported);
        Assert.True(recorder.StopRequests > 0);
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task ARecorderThatIgnoresSigtermIsKilled()
    {
        var recorder = new FakeRecorderProcess { HonoursStop = false, HonoursKill = true };
        using var capture = new PipeWireCapture(takes, () => recorder);

        await capture.StartRecording();
        recorder.Feed(0.5f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));

        long began = Stopwatch.GetTimestamp();
        await capture.StopRecording();
        TimeSpan took = Stopwatch.GetElapsedTime(began);

        Assert.True(recorder.StopRequests > 0, "the stop asks before it insists");
        Assert.True(recorder.Kills > 0, "a recorder that ignores the request is killed");
        Assert.True(recorder.Disposed);
        Assert.True(took < CaptureStopBudget.Total * 2, $"the stop took {took}");
    }

    [Fact]
    public async Task AStopReturnsEvenWhenNothingCanBeKilled()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess { HonoursStop = false, HonoursKill = false };
        using var capture = new PipeWireCapture(takes, () => recorder);

        await capture.StartRecording();
        recorder.Feed(0.5f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));

        long began = Stopwatch.GetTimestamp();
        (string url, _) = await capture.StopRecording();
        TimeSpan took = Stopwatch.GetElapsedTime(began);

        // The whole point: the recording is on disk and the session goes on, while a process this
        // machine cannot get rid of is still holding the microphone.
        Assert.True(File.Exists(url));
        Assert.True(took < CaptureStopBudget.Total * 2, $"the stop took {took}");
        Assert.Equal("false", diagnostics.Field("capture.stop.end", "device_released"));
        Assert.NotNull(diagnostics.Event("capture.stop.device-abandoned"));

        // And the reader that outlived the stop reaches a session that refuses it, instead of the
        // finished take or whatever session runs next.
        (long frames, long samples) = capture.DeliveredCounts;
        recorder.Feed(0.9f, 0.9f, 0.9f);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal((frames, samples), capture.DeliveredCounts);

        recorder.Die();
    }

    [Fact]
    public async Task TwoRecordingsAtOnceAreRefused()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);

        await capture.StartRecording();
        await Assert.ThrowsAsync<AudioCaptureError.EngineUnavailable>(capture.StartRecording);

        await capture.StopRecording();
    }

    [Fact]
    public async Task StoppingWhatIsNotRecordingSaysSo()
    {
        using var capture = new PipeWireCapture(takes, () => new FakeRecorderProcess());

        await Assert.ThrowsAsync<AudioCaptureError.NotRecording>(capture.StopRecording);
    }

    [Fact]
    public async Task AnAbortLeavesNothingBehind()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, () => recorder);

        string path = await capture.StartRecording();
        recorder.Feed(0.5f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));

        await capture.AbortRecording();

        Assert.False(File.Exists(path));
        Assert.Null(await capture.TakeBufferedSamples());
        Assert.True(recorder.Disposed);
        Assert.NotNull(diagnostics.Event("capture.abort"));
    }

    [Fact]
    public async Task EachRecordingIsItsOwn()
    {
        var first = new FakeRecorderProcess();
        var second = new FakeRecorderProcess();
        var recorders = new Queue<FakeRecorderProcess>([first, second]);
        using var capture = new PipeWireCapture(takes, recorders.Dequeue);

        await capture.StartRecording();
        first.Feed([.. Enumerable.Repeat(0.25f, 2_000)]);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        await capture.StopRecording();
        Assert.Equal(2_000L, capture.DeliveredCounts.Samples);

        await capture.StartRecording();
        second.Feed([.. Enumerable.Repeat(-0.25f, 100)]);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        await capture.StopRecording();

        // Counts, samples and latency all belong to the recording that just ended - the previous
        // one's frames are not still being added to them.
        Assert.Equal(100L, capture.DeliveredCounts.Samples);
        float[]? buffered = await capture.TakeBufferedSamples();
        Assert.Equal(100, buffered!.Length);
        Assert.All(buffered, sample => Assert.Equal(-0.25f, sample));
    }
}
