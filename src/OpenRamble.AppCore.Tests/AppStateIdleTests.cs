using System.Runtime.CompilerServices;
using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Application life between dictations.
///
/// It sits in the tray for weeks doing nothing. Everything it does in that time is pure cost, and
/// everything it fails to do when the computer wakes or the headphones are pulled is a stuck
/// dictation with a live microphone.
/// </summary>
public sealed class AppStateIdleTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    private FakeCapture Capture => harness.Capture;

    private FakeOverlay Overlay => harness.Overlay;

    public void Dispose() => harness.Dispose();

    // MARK: - Permission polling

    /// <summary>
    /// The poll keeps running even when everything is granted, deliberately: a revoke sends no
    /// event, and a silent key is indistinguishable from a breakdown. The reasoning is in
    /// <see cref="PermissionPollPolicy"/>.
    /// </summary>
    [Fact]
    public void Scenario001()
    {
        harness.PermissionPollInterval = TimeSpan.FromSeconds(1);

        AppState state = harness.MakeState();

        Assert.Equal(TimeSpan.FromSeconds(1), state.PermissionPollingInterval);
        Assert.True(state.IsPollingPermissions);
    }

    /// <summary>While the permission is missing, the person is in Settings waiting for the screen
    /// to answer.</summary>
    [Fact]
    public void Scenario002()
    {
        harness.PermissionPollInterval = TimeSpan.FromSeconds(1);
        harness.Permissions.MicrophoneGranted = false;

        Assert.Equal(TimeSpan.FromSeconds(1), harness.MakeState().PermissionPollingInterval);
    }

    [Fact]
    public void Scenario003()
    {
        harness.PermissionPollInterval = TimeSpan.FromSeconds(1);
        harness.Permissions.MicrophoneGranted = false;
        AppState state = harness.MakeState();
        Assert.Equal(TimeSpan.FromSeconds(1), state.PermissionPollingInterval);

        harness.Permissions.MicrophoneGranted = true;
        state.RefreshPermissions();

        Assert.Equal(TimeSpan.FromSeconds(1), state.PermissionPollingInterval);
    }

    [Fact]
    public void Scenario004()
    {
        harness.PermissionPollInterval = TimeSpan.Zero;

        AppState state = harness.MakeState();

        Assert.Equal(TimeSpan.Zero, state.PermissionPollingInterval);
        Assert.False(state.IsPollingPermissions);
    }

    // MARK: - Timers at rest

    /// <summary>
    /// At rest the preparation countdown does not tick: the application has no right to spin
    /// anything in the background simply because it is running.
    /// </summary>
    [Fact]
    public void Scenario011()
    {
        AppState state = harness.MakeState();

        Assert.False(state.IsCountingEnginePreparation);
        Assert.Equal(EnginePreparationState.Phase.Idle, state.EnginePreparation.PhaseValue);
        Assert.False(state.IsAwaitingIdleUnload);
    }

    /// <summary>Recording runs without a single repeating timer.</summary>
    [Fact]
    public async Task Scenario005()
    {
        AppState state = await ReadyState();

        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }

    // MARK: - Microphone

    /// <summary>The direct product promise: the recording light goes off when we are not listening.</summary>
    [Fact]
    public async Task Scenario006()
    {
        AppState state = await ReadyState();

        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);

        Assert.False(Capture.IsRecording);
    }

    [Fact]
    public async Task Scenario007()
    {
        AppState state = await ReadyState();

        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnEscape!();
        await TestWait.For("dictation cancelled", () => state.DictationStateValue == DictationState.Idle);

        Assert.False(Capture.IsRecording);
    }

    // MARK: - Sleep and waking

    /// <summary>
    /// The machine fell asleep mid-dictation. A key release that happens during sleep never
    /// reaches us: without stopping, the session would stay in "listening" forever - microphone
    /// on, indicator lit - and the only way out would be Escape.
    /// </summary>
    [Fact]
    public async Task Scenario008()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        harness.SystemEvents.PostWillSleep();

        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.False(Capture.IsRecording);
    }

    /// <summary>What was said before sleep is not thrown away: the person already said it.</summary>
    [Fact]
    public async Task Scenario009()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        harness.SystemEvents.PostWillSleep();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);

        Assert.Equal(1, Capture.StopCount);
        Assert.Equal(0, Capture.AbortCount);
        // The explanation has to reach the screen. Said in the middle of the stop it never
        // arrives: the kernel immediately redraws the panel under "transcribing".
        await TestWait.For(
            "the explanation reached the panel",
            () => Overlay.Notices.Any(notice => notice.Message.Contains("sleep")));
        Assert.Equal(DictationNoticeKind.Info, state.LastNotice?.Kind);
    }

    /// <summary>
    /// Sleep in the middle of a dictation without holding the key. In that mode releasing means
    /// nothing: the recording continues until the next press, and an ordinary stop would pass by.
    /// </summary>
    [Fact]
    public async Task Scenario010()
    {
        AppState state = await ReadyState();
        Monitor.OnDoubleTap!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Assert.True(state.IsHandsFreeActive);

        harness.SystemEvents.PostWillSleep();

        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.False(Capture.IsRecording);
    }

    /// <summary>The app caught the sleep before the first frame of sound - nothing was recorded.</summary>
    [Fact]
    public async Task Scenario011a()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();
        Assert.Equal(DictationState.Preparing, state.DictationStateValue);

        harness.SystemEvents.PostWillSleep();

        await TestWait.For("the session closed", () => state.DictationStateValue == DictationState.Idle);
        Assert.False(Capture.IsRecording);
    }

    [Fact]
    public async Task Scenario012()
    {
        AppState state = await ReadyState();

        harness.SystemEvents.PostWillSleep();
        await Task.Delay(80);

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
        Assert.Null(state.LastNotice);
        Assert.Equal(0, Capture.StartCount);
    }

    /// <summary>
    /// After waking, tracking starts again. The key was released during sleep but no event said
    /// so: the monitor still considers it held and would swallow the next press.
    /// </summary>
    [Fact]
    public async Task Scenario013()
    {
        AppState state = harness.MakeState();
        Assert.True(Monitor.IsRunning);
        int stopsBefore = Monitor.StopCount;

        harness.SystemEvents.PostDidWake();
        await TestWait.For("monitoring restarted", () => Monitor.StopCount > stopsBefore);

        Assert.True(Monitor.IsRunning);
        Assert.True(state.MicrophoneGranted);
    }

    // MARK: - Audio device change

    /// <summary>
    /// The headphones were pulled out mid-sentence. The engine keeps running but frames no longer
    /// reach it: the person speaks into silence and would learn about it only from an empty
    /// result.
    /// </summary>
    [Fact]
    public async Task Scenario014()
    {
        AppState state = await ReadyState(TimeSpan.FromSeconds(2));
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        harness.SystemEvents.PostAudioDeviceChanged();

        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
        await TestWait.For("the WAV was kept for safety", () => harness.RecoveryFiles().Count > 0);
        Assert.Equal(DictationNoticeKind.Failure, state.LastNotice?.Kind);
        Assert.Contains("audio device was disconnected", state.LastNotice?.Message);
        Assert.False(Capture.IsRecording);
    }

    /// <summary>After a disconnect, recognition does not start on the truncated audio.</summary>
    [Fact]
    public async Task Scenario015()
    {
        harness.Transcription.Error = new ASREngineError.ModelsNotLoaded();
        AppState state = await ReadyState(TimeSpan.FromSeconds(2));
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        harness.SystemEvents.PostAudioDeviceChanged();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);

        Assert.Contains(Overlay.Notices, notice => notice.Message.Contains("audio device was disconnected"));
        Assert.DoesNotContain(Overlay.Notices, notice => notice.Message.Contains("transcribe"));
    }

    /// <summary>
    /// Devices change just like that: a monitor was plugged in, the headphones went to sleep. As
    /// long as we are not listening it is none of our business.
    /// </summary>
    [Fact]
    public async Task Scenario016()
    {
        AppState state = await ReadyState();

        harness.SystemEvents.PostAudioDeviceChanged();
        await Task.Delay(80);

        Assert.Null(state.LastNotice);
        Assert.Equal(DictationState.Idle, state.DictationStateValue);
    }

    // MARK: - Downloading the model

    /// <summary>
    /// Opening settings must not erase a download in progress. The Model tab inspects the disk
    /// when it appears; there is no ready marker yet at download time, so the inspection declared
    /// the model uninstalled - and the person who came to look at the progress saw a
    /// "Download Model" button instead.
    /// </summary>
    [Fact]
    public async Task Scenario017()
    {
        AppState state = harness.MakeState();
        state.InstallModel();
        await TestWait.For("the download started", () => state.ModelStateValue is ModelState.Downloading);

        await state.RefreshModelState();

        Assert.IsType<ModelState.Downloading>(state.ModelStateValue);

        harness.Downloader.Release();
        await TestWait.For("the download finished", () => state.ModelStateValue is not ModelState.Downloading);
    }

    /// <summary>
    /// A failed install must keep its exact error and Retry action on screen. Refreshing the empty
    /// install directory used to replace this state with "Model not installed" immediately, making
    /// the real network error flash by.
    /// </summary>
    [Fact]
    public async Task FailedModelDownloadRemainsVisible()
    {
        AppState state = harness.MakeState();
        state.InstallModel();
        await TestWait.For("the download started", () => harness.Downloader.HasStarted);

        // A permanent failure, deliberately: transient ones are retried, and a retried download is
        // still a download - it must not flash "failed" between attempts.
        harness.Downloader.Release(new ModelDownloadError.HttpStatus(404));
        await TestWait.For("the failure surfaced", () => state.ModelStateValue is ModelState.Failed);

        var failed = Assert.IsType<ModelState.Failed>(state.ModelStateValue);
        var download = Assert.IsType<ModelStoreError.Download>(failed.Error);
        Assert.Contains("404", download.Reason);
    }

    // MARK: - Many cycles in a row

    /// <summary>
    /// Two hundred dictations in a row.
    ///
    /// The application lives for weeks, and a leak the size of one object per dictation is
    /// invisible on the second lap and on the tenth.
    /// </summary>
    /// <remarks>
    /// macOS measures the process's resident size through <c>mach_task_basic_info</c>. Here the
    /// managed heap after a forced collection is the honest equivalent and the one this port can
    /// actually leak into; resident size on a server GC would mostly measure the collector's mood.
    /// </remarks>
    [Fact]
    public async Task Scenario018()
    {
        AppState state = await ReadyState();

        // Warm-up: the first laps raise internal buffers and caches, and without it their
        // one-time price would look like a leak.
        for (int lap = 0; lap < 20; lap++) { await RunCycle(state); }

        long before = GC.GetTotalMemory(forceFullCollection: true);

        for (int lap = 0; lap < 200; lap++) { await RunCycle(state); }

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long grown = after > before ? after - before : 0;
        Assert.True(grown < 4 * 1024 * 1024, $"over 200 dictations the managed heap grew by {grown / 1024} KB");

        // Every started recording must be closed - otherwise the microphone would stay on and the
        // counts would silently drift apart.
        Assert.Equal(220, Capture.StartCount);
        Assert.Equal(Capture.StartCount, Capture.StopCount + Capture.AbortCount);
        Assert.Equal(DictationState.Idle, state.DictationStateValue);
    }

    /// <summary>
    /// After work, the whole application is released.
    /// </summary>
    /// <remarks>
    /// macOS relies on <c>[weak self]</c> inside notification blocks, so the object dies as soon
    /// as the last owner drops it. .NET events hold their targets strongly, so here the same
    /// promise is stated as: <see cref="AppState.Dispose"/> really does cut every edge, and
    /// nothing else in the harness keeps the object alive afterwards.
    /// </remarks>
    [Fact]
    public async Task Scenario019()
    {
        WeakReference weak = await RunAndRelease();

        for (int attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(10);
        }

        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> RunAndRelease()
    {
        AppState state = await ReadyState();
        for (int lap = 0; lap < 5; lap++) { await RunCycle(state); }
        state.Dispose();
        return new WeakReference(state);
    }

    // MARK: - Engine lifecycle

    /// <summary>
    /// A recovery that fails still gives the engine back: one wedge must not be terminal.
    ///
    /// The recycle unloads and reloads, and the unload that opens it can overtake the reload it
    /// started - which arrives as cancellation, not as a broken model. If that ended the recovery
    /// task uncaught, the "one wedge, one cure" guard stayed raised for the life of the process
    /// and the key kept starting dictations nothing could finish. And a load that ended without
    /// an answer is not the end of a preparation: the standing rule sees a ready model with a
    /// cold engine and nothing running, and brings the engine back by itself.
    /// </summary>
    [Fact]
    public async Task ARecycleWhoseReloadIsOvertakenStillLetsTheEngineComeBack()
    {
        harness.InstallModelMarker();
        var engine = new WedgingASREngine();
        harness.WarmUpEngine = engine;
        harness.EngineWarmupRetryDelay = TimeSpan.FromMilliseconds(5);
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        // Waking finds a wedged inference and starts the recycle; the recycle's own reload is
        // overtaken and reports cancellation.
        engine.FailNextInference();
        engine.CancelNextLoad();
        harness.SystemEvents.PostDidWake();

        await TestWait.For("the wedged engine was let go", () => !state.IsEngineReady);
        await TestWait.For("the recycle tried to reload", () => engine.ModelLoads >= 2);

        // An overtaken load is not a damaged model: offering a redownload here would be a lie
        // about files that are perfectly intact.
        Assert.True(state.ModelStateValue.IsReady);
        Assert.True(state.IsDictationReady, "the press must still start a recording");

        // The engine comes back without anybody pressing anything - possible only if the
        // recovery flag fell when the failed recycle ended and the standing rule then spoke.
        await TestWait.For("the engine came back", () => state.IsEngineReady);
        Assert.Equal(1, engine.Unloads);
    }

    /// <summary>A healthy, idle app arms the idle-unload countdown and nothing else.</summary>
    [Fact]
    public async Task AReadyEngineAtRestArmsOnlyTheIdleCountdown()
    {
        harness.InstallModelMarker();
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });
        harness.WarmUpEngine = new CountingASREngine();
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        Assert.True(state.IsAwaitingIdleUnload, "five minutes of idleness returns the memory");
        Assert.False(state.IsCountingEnginePreparation);
        Assert.False(state.IsPollingPermissions);
    }

    // MARK: - Helpers

    /// <summary>
    /// The application with the permission granted and the model laid out.
    ///
    /// The recording duration is below the minimum, so the session closes without entering
    /// recognition: there is still no real model on disk.
    /// </summary>
    private async Task<AppState> ReadyState(TimeSpan? recordingDuration = null)
    {
        harness.InstallModelMarker();
        Capture.SetDuration(recordingDuration ?? TimeSpan.FromSeconds(0.1));
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.True(state.IsDictationReady);
        return state;
    }

    private async Task RunCycle(AppState state)
    {
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }
}
