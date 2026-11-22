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

    /// <summary>
    /// A healthy disk lets a dictation outlive the in-memory cap: memory is given up, the file
    /// keeps every sample, and nothing stops the take (upstream 0.30.4).
    /// </summary>
    [Fact]
    public async Task AHealthyDiskCarriesTheTakePastTheMemoryCap()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ => recorder, maximumBufferedSamples: 1_000);
        bool limitReached = false;
        AudioCaptureError? failure = null;
        capture.OnMemoryLimitReached = _ => limitReached = true;
        capture.Failed = (_, error) => failure = error;

        await capture.StartRecording();
        recorder.Feed(new float[4_000]);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        (string url, TimeSpan duration) = await capture.StopRecording();

        Assert.Equal(TimeSpan.FromSeconds(4_000 / 16_000.0), duration);
        Assert.Null(await capture.TakeBufferedSamples());
        Assert.False(limitReached, "a take with a healthy file is never stopped at the cap");
        Assert.Null(failure);
        Assert.Equal(44 + (4_000 * 2), new FileInfo(url).Length);
    }

    [Fact]
    public async Task TheTakeHoldsExactlyWhatTheRecorderProduced()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ => recorder);

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
        using var capture = new PipeWireCapture(takes, _ => recorder);
        var levels = new List<float>();
        capture.OnInputLevel = level => levels.Add(level);

        await capture.StartRecording();
        recorder.Feed(0.1f, -0.75f, 0.2f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        await capture.StopRecording();

        Assert.NotEmpty(levels);
        Assert.Equal(0.75f, levels.Max());
    }

    /// <summary>
    /// A long take leaves in pieces while it runs, and the count reported at the stop is exactly
    /// where the tail begins - the number the controller decodes from, so a wrong one either
    /// loses words or says them twice.
    /// </summary>
    [Fact]
    public async Task SegmentsLeaveDuringTheTakeAndTheCountSaysWhereTheTailBegins()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ => recorder);
        var segments = new List<float[]>();
        await capture.SetSegmentSink(piece => { lock (segments) { segments.Add(piece); } });

        await capture.StartRecording();
        // Sixteen seconds of speech, a pause long enough to earn a cut past the shipped floor,
        // and a tail that stays pending.
        float[] speech = [.. Enumerable.Repeat(0.5f, 16 * 16_000)];
        float[] pause = new float[16_000 * 3 / 2];
        float[] tail = [.. Enumerable.Repeat(0.5f, 2 * 16_000)];
        recorder.Feed(speech);
        recorder.Feed(pause);
        recorder.Feed(tail);
        long expected = speech.Length + pause.Length + tail.Length;
        var patience = Stopwatch.StartNew();
        while (capture.DeliveredCounts.Samples < expected && patience.Elapsed < Patience)
        {
            await Task.Delay(10);
        }

        await capture.StopRecording();
        float[]? buffered = await capture.TakeBufferedSamples();
        int consumed = await capture.ConsumedSampleCount();

        Assert.NotNull(buffered);
        Assert.Equal(expected, buffered.Length);
        float[] segment;
        lock (segments) { segment = Assert.Single(segments); }
        Assert.Equal(segment.Length, consumed);
        Assert.True(consumed > speech.Length && consumed < expected, "the seam sits inside the pause");
        // The segment is the take's own prefix, sample for sample: what left early is exactly
        // what the tail no longer has to cover.
        Assert.Equal(buffered.AsSpan(0, consumed).ToArray(), segment);
    }

    /// <summary>
    /// A stop whose take cannot be closed still lets go of the recorder: the failure is the
    /// stop's to report, the microphone is not its to keep.
    /// </summary>
    [Fact]
    public async Task AStopThatFailsStillReleasesTheRecorder()
    {
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ => recorder);
        // A segment sink that breaks the frame queue at the first cut.
        await capture.SetSegmentSink(_ => throw new InvalidOperationException("segment sink broke"));

        await capture.StartRecording();
        float[] speech = [.. Enumerable.Repeat(0.5f, 16 * 16_000)];
        float[] pause = new float[16_000 * 3 / 2];
        recorder.Feed(speech);
        recorder.Feed(pause);
        long expected = speech.Length + pause.Length;
        var patience = Stopwatch.StartNew();
        while (capture.DeliveredCounts.Samples < expected && patience.Elapsed < Patience)
        {
            await Task.Delay(10);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(capture.StopRecording);

        Assert.True(recorder.Disposed, "a failed stop must not keep the microphone");
    }

    [Fact]
    public async Task StartingSaysWhatItStartedAndInWhatFormat()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ => recorder);

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
        using var capture = new PipeWireCapture(takes, _ => recorder);

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
            _ => throw new AudioCaptureError.EngineUnavailable(
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
        using var capture = new PipeWireCapture(takes, _ => recorder);
        AudioCaptureError? reported = null;
        capture.Failed = (_, error) => reported = error;

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
        using var capture = new PipeWireCapture(takes, _ => recorder);
        int alarms = 0;
        capture.Failed = (_, _) => alarms += 1;

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
        using var capture = new PipeWireCapture(takes, _ => recorder);
        AudioCaptureError? reported = null;
        capture.Failed = (_, error) => reported = error;

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
        using var capture = new PipeWireCapture(takes, _ => recorder);

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
        using var capture = new PipeWireCapture(takes, _ => recorder);

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
        using var capture = new PipeWireCapture(takes, _ => recorder);

        await capture.StartRecording();
        await Assert.ThrowsAsync<AudioCaptureError.EngineUnavailable>(capture.StartRecording);

        await capture.StopRecording();
    }

    [Fact]
    public async Task ANewerSessionReclaimsAStartItsWatchdogAlreadyGaveUpOn()
    {
        using var launching = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var wedged = new FakeRecorderProcess();
        var retry = new FakeRecorderProcess();
        var recorders = new Queue<FakeRecorderProcess>([wedged, retry]);
        using var capture = new PipeWireCapture(takes, _ =>
        {
            FakeRecorderProcess next;
            lock (recorders) { next = recorders.Dequeue(); }
            if (ReferenceEquals(next, wedged))
            {
                launching.Set();
                release.Wait(Patience);
            }

            return next;
        });

        // Session 1's recorder is still coming up when the person, already told the microphone
        // took too long, presses again.
        Task<string> stale = Task.Run(() => capture.StartRecording(1, null));
        Assert.True(launching.Wait(Patience));

        string path = await capture.StartRecording(2, null);
        release.Set();

        await Assert.ThrowsAsync<AudioCaptureError.NotRecording>(() => stale);
        Assert.True(wedged.StopRequests > 0 || wedged.Kills > 0, "the stale recorder must not keep the microphone");
        retry.Feed(0.5f);
        (string url, _) = await capture.StopRecording(2);
        Assert.Equal(path, url);
    }

    /// <summary>
    /// A newer start that cannot open its own take still takes the older one down: the older one
    /// was already given up, and nobody else will ever stop it.
    /// </summary>
    [Fact]
    public async Task ASupersedingStartThatCannotOpenItsTakeStillReleasesTheOlderOne()
    {
        string folder = Path.Combine(takes, "takes");
        var stale = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(folder, _ => stale);

        await capture.StartRecording(1, null);
        stale.Feed(0.5f);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));
        // The folder is taken away from under the recording and a file is left in its place, so
        // the next take has nowhere to be written.
        Directory.Move(folder, Path.Combine(takes, "moved"));
        await File.WriteAllTextAsync(folder, "occupied");

        await Assert.ThrowsAnyAsync<IOException>(() => capture.StartRecording(2, null));

        Assert.True(stale.StopRequests > 0, "the superseded recorder must not keep the microphone");
        Assert.Null(await capture.TakeBufferedSamples(1));
        var patience = Stopwatch.StartNew();
        while (!stale.Disposed && patience.Elapsed < Patience) { await Task.Delay(10); }
        Assert.True(stale.Disposed);
    }

    /// <summary>
    /// An abort that arrives while the recorder is still being launched leaves it to the start,
    /// which finds itself aborted when the launch returns and stops what it launched.
    /// </summary>
    [Fact]
    public async Task AnAbortWhileTheRecorderIsStillComingUpLeavesNothingBehind()
    {
        using var launching = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ =>
        {
            launching.Set();
            release.Wait(Patience);
            return recorder;
        });

        Task<string> starting = Task.Run(() => capture.StartRecording(1, null));
        Assert.True(launching.Wait(Patience));
        await capture.AbortRecording(1);
        release.Set();

        await Assert.ThrowsAsync<AudioCaptureError.NotRecording>(() => starting);
        Assert.True(recorder.StopRequests > 0, "the launched recorder must not keep the microphone");
        Assert.True(recorder.Disposed);
        Assert.Empty(Directory.EnumerateFiles(takes));
    }

    [Fact]
    public async Task AStaleSessionCannotStopReadOrFailTheNewerTake()
    {
        var first = new FakeRecorderProcess();
        var second = new FakeRecorderProcess();
        var recorders = new Queue<FakeRecorderProcess>([first, second]);
        using var capture = new PipeWireCapture(takes, _ => recorders.Dequeue());
        var failures = new List<int>();
        capture.Failed = (session, _) => { lock (failures) { failures.Add(session); } };

        await capture.StartRecording(1, null);
        await capture.StartRecording(2, null);
        second.Feed([.. Enumerable.Repeat(0.25f, 100)]);
        Assert.True(await capture.WaitForFirstFrame().WaitAsync(Patience));

        // Late calls of session 1's continuations.
        await capture.AbortRecording(1);
        await Assert.ThrowsAsync<AudioCaptureError.NotRecording>(() => capture.StopRecording(1));
        first.Die();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        (string url, TimeSpan duration) = await capture.StopRecording(2);
        Assert.True(File.Exists(url));
        Assert.Null(await capture.TakeBufferedSamples(1));
        Assert.Equal(100, (await capture.TakeBufferedSamples(2))!.Length);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task StoppingWhatIsNotRecordingSaysSo()
    {
        using var capture = new PipeWireCapture(takes, _ => new FakeRecorderProcess());

        await Assert.ThrowsAsync<AudioCaptureError.NotRecording>(capture.StopRecording);
    }

    [Fact]
    public async Task AnAbortLeavesNothingBehind()
    {
        using var diagnostics = new DiagnosticRecorder();
        var recorder = new FakeRecorderProcess();
        using var capture = new PipeWireCapture(takes, _ => recorder);

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
        using var capture = new PipeWireCapture(takes, _ => recorders.Dequeue());

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
