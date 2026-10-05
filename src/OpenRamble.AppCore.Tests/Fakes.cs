using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DictationCore;
using LocalASR;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.AppCore.Tests;

// False edges of the system.
//
// Ported from the macOS `Fakes.swift`. The Accessibility manager and the pasteboard/input-system
// doubles have no counterpart: Windows needs no Accessibility grant, and the insertion edge is
// exercised in OpenRamble.App, not here.

/// <summary>
/// Our own crash-WAV: the payload is already on disk, but the sizes in the header are still zero
/// because the process never reached the writer's close.
/// </summary>
public static class AbandonedWav
{
    public static void Write(string path, int sampleBytes = 3200)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36u);
            writer.Write("WAVEfmt "u8);
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(16_000u);
            writer.Write(32_000u);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write(0u);
            writer.Write(new byte[sampleBytes]);
        }

        File.WriteAllBytes(path, buffer.ToArray());
        // A crash leftover is old by the time the app relaunches. A fresh file would be skipped
        // by the import's live-recording guard - another running instance may still be writing it.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
    }
}

public sealed class FakeCapture : IAudioCapturing
{
    private readonly object gate = new();
    private readonly string file = Path.Combine(
        Path.GetTempPath(),
        $"openramble-test-take-{Guid.NewGuid()}.wav");

    private TimeSpan duration = TimeSpan.FromSeconds(2);

    public int StartCount { get { lock (gate) { return startCount; } } }

    public int StopCount { get { lock (gate) { return stopCount; } } }

    public int AbortCount { get { lock (gate) { return abortCount; } } }

    /// <summary>
    /// Is the microphone listening right now.
    ///
    /// This is what the "the recording light goes off when we are not listening" promise rests
    /// on: it has to be checked by whether the capture is closed, not by the state on screen.
    /// </summary>
    public bool IsRecording { get { lock (gate) { return isRecording; } } }

    private int startCount;
    private int stopCount;
    private int abortCount;
    private bool isRecording;

    public void SetDuration(TimeSpan value) { lock (gate) { duration = value; } }

    public Task<string> StartRecording()
    {
        lock (gate)
        {
            startCount += 1;
            isRecording = true;
        }

        return Task.FromResult(file);
    }

    public Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        TimeSpan value;
        lock (gate)
        {
            stopCount += 1;
            isRecording = false;
            value = duration;
        }

        File.WriteAllText(file, "RIFF-test-audio");
        return Task.FromResult((file, value));
    }

    public Task AbortRecording()
    {
        lock (gate)
        {
            abortCount += 1;
            isRecording = false;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeInserter : ITextInserting
{
    private readonly object gate = new();
    private readonly List<string> inserted = [];
    private TextInsertionError? error;
    private TaskCompletionSource? hold;

    public IReadOnlyList<string> InsertedTexts { get { lock (gate) { return [.. inserted]; } } }

    public TargetApplication? Frontmost { get; set; }

    public void SetError(TextInsertionError? value) { lock (gate) { error = value; } }

    /// <summary>
    /// Keep the insertion open until the test lets it finish.
    ///
    /// In life, <c>Inserting</c> lasts as long as the paste takes in someone else's application -
    /// long enough for a person to press Escape. Without a hold the state passes between two
    /// lines of the test and what the application says at that moment cannot be checked at all.
    /// </summary>
    public void HoldInsertions()
    {
        lock (gate) { hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
    }

    public void ReleaseInsertions()
    {
        TaskCompletionSource? pending;
        lock (gate)
        {
            pending = hold;
            hold = null;
        }

        pending?.TrySetResult();
    }

    public async Task Insert(string text, TargetApplication? target)
    {
        Task? pending;
        lock (gate) { pending = hold?.Task; }
        if (pending is not null) { await pending.ConfigureAwait(false); }

        lock (gate)
        {
            if (error is TextInsertionError failure) { throw failure; }
            inserted.Add(text);
        }
    }

    public Task PressReturn() => Task.CompletedTask;

    public TargetApplication? FrontmostApplication() => Frontmost;
}

public sealed class FakeOverlay : IOverlayPresenting, IEngineWaitPresenting
{
    private readonly object gate = new();
    private readonly List<DictationState> presented = [];
    private readonly List<DictationNotice> notices = [];
    private readonly List<bool> engineWaits = [];
    private int dismissCount;

    /// <summary>Every "waiting for the model" announcement and retraction, in order.</summary>
    public IReadOnlyList<bool> EngineWaits { get { lock (gate) { return [.. engineWaits]; } } }

    public void SetWaitingForEngine(bool waiting)
    {
        lock (gate) { engineWaits.Add(waiting); }
    }

    public IReadOnlyList<DictationState> PresentedStates { get { lock (gate) { return [.. presented]; } } }

    public IReadOnlyList<DictationNotice> Notices { get { lock (gate) { return [.. notices]; } } }

    public int DismissCount { get { lock (gate) { return dismissCount; } } }

    public Task Present(DictationState state, TimeSpan elapsed)
    {
        lock (gate) { presented.Add(state); }
        return Task.CompletedTask;
    }

    public Task Dismiss()
    {
        lock (gate) { dismissCount += 1; }
        return Task.CompletedTask;
    }

    public Task PresentNotice(DictationNotice notice)
    {
        lock (gate) { notices.Add(notice); }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Deliberately free of any thread affinity: the permission poll reads this off the pool,
/// exactly like the real implementation.
/// </summary>
public sealed class FakePermissions : IPermissionReading
{
    private volatile bool microphone = true;

    public bool MicrophoneGranted
    {
        get => microphone;
        set => microphone = value;
    }
}

public sealed class FakeMicrophoneConsentFlow : IMicrophoneConsentFlow
{
    private readonly FakePermissions permissions;

    public FakeMicrophoneConsentFlow(FakePermissions permissions)
    {
        this.permissions = permissions;
    }

    public bool RequestResult { get; set; } = true;

    public int RequestCount { get; private set; }

    public int OpenSettingsCount { get; private set; }

    public int ActivationCount { get; private set; }

    public Task<bool> Request()
    {
        RequestCount += 1;
        if (RequestResult) { permissions.MicrophoneGranted = true; }
        return Task.FromResult(RequestResult);
    }

    public void OpenSettings() => OpenSettingsCount += 1;

    public void ActivateApplication() => ActivationCount += 1;
}

public sealed class FakeHotkeyMonitor : IHotkeyMonitoring
{
    public Action? OnPress { get; set; }

    public Action? OnRelease { get; set; }

    public Action? OnDoubleTap { get; set; }

    public Action? OnSingleTapWhileHandsFree { get; set; }

    public Action? OnAbortShortcut { get; set; }

    public Action? OnEscape { get; set; }

    public Action? OnHotkeyDown { get; set; }

    public Action? OnHotkeyUp { get; set; }

    public Action<string>? OnTrackingFailure { get; set; }

    public bool IsHandsFreeActive { get; set; }

    public bool IsRunning { get; private set; }

    public DictationHotkey? Hotkey { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public void SetHotkey(DictationHotkey hotkey) => Hotkey = hotkey;

    public void Start()
    {
        StartCount += 1;
        IsRunning = true;
    }

    public void Stop()
    {
        StopCount += 1;
        IsRunning = false;
    }

    /// <summary>One whole press: down, then up. The real monitor raises these around the gesture.</summary>
    public void Tap()
    {
        OnHotkeyDown?.Invoke();
        OnHotkeyUp?.Invoke();
    }
}

/// <summary>
/// Notification centres of our own, not the system's: a test must neither hear the real machine
/// go to sleep nor wake somebody else's subscribers.
/// </summary>
public sealed class FakeSystemEvents : ISystemEventSource
{
    public event Action? WillSleep;

    public event Action? DidWake;

    public event Action? AudioDeviceChanged;

    public event Action? ApplicationBecameActive;

    public event Action<MemoryPressureTier>? MemoryPressureChanged;

    public void PostWillSleep() => WillSleep?.Invoke();

    public void PostDidWake() => DidWake?.Invoke();

    public void PostAudioDeviceChanged() => AudioDeviceChanged?.Invoke();

    public void PostApplicationBecameActive() => ApplicationBecameActive?.Invoke();

    public void PostMemoryPressure(MemoryPressureTier tier) => MemoryPressureChanged?.Invoke(tier);
}

/// <summary>The second watcher, as a test sees it: which chord it was given and whether it runs.</summary>
public sealed class FakeShortcutMonitor : IShortcutMonitoring
{
    public Action? OnPress { get; set; }

    public bool IsRunning { get; private set; }

    public KeyCombination? Shortcut { get; private set; }

    public void SetShortcut(KeyCombination? shortcut)
    {
        Shortcut = shortcut;
        if (shortcut is null) { Stop(); }
    }

    public void Start()
    {
        if (Shortcut is null) { return; }
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    /// <summary>The chord arrived on the keyboard.</summary>
    public void Press() => OnPress?.Invoke();
}

/// <summary>The machine's microphones, as the test decides they are.</summary>
public sealed class FakeInputDevices : IAudioInputDeviceSource
{
    public List<AudioInputDevice> Devices { get; } = [];

    public IReadOnlyList<AudioInputDevice> Available() => [.. Devices];
}

public sealed class FakeLaunchAtLogin : ILaunchAtLoginService
{
    public bool IsEnabled { get; private set; }

    public Exception? Failure { get; set; }

    public void SetEnabled(bool enabled)
    {
        if (Failure is Exception failure) { throw failure; }
        IsEnabled = enabled;
    }
}

public sealed class FakeClipboard : IHostOnlyClipboard
{
    private readonly List<string> copied = [];

    public IReadOnlyList<string> Copied => copied;

    public Exception? Failure { get; set; }

    public void Copy(string text)
    {
        if (Failure is Exception failure) { throw failure; }
        copied.Add(text);
    }
}

public sealed class FakeFolders : IFolderOpening
{
    private readonly List<string> opened = [];

    public IReadOnlyList<string> Opened => opened;

    public void Open(string path) => opened.Add(path);
}

public sealed class FakeSounds : ISounding
{
    private int played;

    public int Played => Volatile.Read(ref played);

    public Task PlayAttention()
    {
        Interlocked.Increment(ref played);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A dummy field of somebody else's application: counts how many times it was looked into.
///
/// The captures are counted, not the result: the promise "switched off means not read" is broken
/// the moment the field is reached for, even if what was read is thrown away.
/// </summary>
public sealed class FakeFocusedField : IFocusedFieldReading
{
    private int captured;

    public string? Value { get; set; }

    public int Captured => Volatile.Read(ref captured);

    public FocusedFieldHandle? CaptureFocusedField()
    {
        Interlocked.Increment(ref captured);
        return new FocusedFieldHandle(() => Value);
    }
}

/// <summary>The known recognition answer.</summary>
public sealed class FakeTranscription
{
    public string Text { get; set; } = "Ping test";

    public Exception? Error { get; set; }

    public TimeSpan? Delay { get; set; }
}

/// <summary>
/// A downloader that goes nowhere and does not let go until it is allowed to.
///
/// Needed to catch the application in the "downloading" state and see what the other screens do
/// with it at that moment.
/// </summary>
public sealed class BlockingModelDownloader : IModelDownloading
{
    private readonly object gate = new();
    private bool started;
    private bool released;
    /// <summary>
    /// A released download ends in cancellation by default: it is not retried, so the store
    /// reaches a terminal state at once instead of walking the retry and mirror ladder.
    /// </summary>
    private ModelDownloadError terminal = new ModelDownloadError.Cancelled();

    public bool HasStarted { get { lock (gate) { return started; } } }

    public void Release(ModelDownloadError? throwing = null)
    {
        lock (gate)
        {
            terminal = throwing ?? new ModelDownloadError.Cancelled();
            released = true;
        }
    }

    public async Task<string> Download(
        Uri url,
        long expectedBytes,
        Action<long> onProgress,
        CancellationToken cancellationToken = default)
    {
        lock (gate) { started = true; }

        onProgress(expectedBytes / 2);

        while (true)
        {
            lock (gate)
            {
                if (released) { throw terminal; }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// An engine the model files of which are intact but which refuses to load.
///
/// Exactly this kind of failure happens in life: the SHA-256 check passed, but the session would
/// not come up. A disk inspection cannot detect the state.
/// </summary>
public sealed class FailingASREngine : IASREngineAdapting
{
    public Task LoadModels(string directory) =>
        Task.FromException(new ASREngineError.ModelsUnavailable("the engine failed loading"));

    public Task<ASRResult> Transcribe(float[] samples) =>
        Task.FromException<ASRResult>(new ASREngineError.ModelsNotLoaded());

    public Task Unload() => Task.CompletedTask;
}

/// <summary>An engine that counts loads, unloads and inferences.</summary>
public sealed class CountingASREngine : IASREngineAdapting
{
    private int modelLoads;
    private int unloads;
    private int inferences;

    public int ModelLoads => Volatile.Read(ref modelLoads);

    public int Unloads => Volatile.Read(ref unloads);

    public int Inferences => Volatile.Read(ref inferences);

    public Task LoadModels(string directory)
    {
        Interlocked.Increment(ref modelLoads);
        return Task.CompletedTask;
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        Interlocked.Increment(ref inferences);
        return Task.FromResult(new ASRResult(string.Empty, TimeSpan.Zero, TimeSpan.Zero));
    }

    public Task Unload()
    {
        Interlocked.Increment(ref unloads);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An engine that can be made to wedge once and to lose one reload.
/// </summary>
/// <remarks>
/// Both halves happen together in life, and together they are the trap: an inference stuck on
/// something that died triggers the recycle, and the recycle's own reload is then overtaken by
/// the unload that opened it - which reaches the owner as cancellation rather than as a broken
/// model. A recovery that does not survive that leaves the engine permanently un-recyclable,
/// while the person's key keeps starting dictations nothing can finish.
/// </remarks>
public sealed class WedgingASREngine : IASREngineAdapting
{
    private readonly Lock gate = new();
    private int modelLoads;
    private int inferences;
    private int unloads;
    private bool cancelNextLoad;
    private bool failNextInference;

    public int ModelLoads => Volatile.Read(ref modelLoads);

    public int Inferences => Volatile.Read(ref inferences);

    public int Unloads => Volatile.Read(ref unloads);

    /// <summary>The next load is overtaken by an unload and reports cancellation.</summary>
    public void CancelNextLoad() { lock (gate) { cancelNextLoad = true; } }

    /// <summary>The next inference is the one stuck on a service that died.</summary>
    public void FailNextInference() { lock (gate) { failNextInference = true; } }

    public Task LoadModels(string directory)
    {
        Interlocked.Increment(ref modelLoads);
        bool cancel;
        lock (gate)
        {
            cancel = cancelNextLoad;
            cancelNextLoad = false;
        }

        return cancel ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask;
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        Interlocked.Increment(ref inferences);
        bool fail;
        lock (gate)
        {
            fail = failNextInference;
            failNextInference = false;
        }

        return fail
            ? Task.FromException<ASRResult>(new ASREngineError.InferenceFailed("the engine is wedged"))
            : Task.FromResult(new ASRResult(string.Empty, TimeSpan.Zero, TimeSpan.Zero));
    }

    public Task Unload()
    {
        Interlocked.Increment(ref unloads);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An engine whose loads follow a written script.
/// </summary>
/// <remarks>
/// The existing fakes either always succeed or always fail. Neither can express the shape that
/// strands a first run: an attempt that ends without an answer, followed by one that would have
/// worked.
/// </remarks>
public sealed class ScriptedLoadEngine : IASREngineAdapting
{
    public enum Outcome
    {
        /// <summary>
        /// The load ends without an answer - a cancelled request, an overtaken generation, a
        /// momentary disagreement about the model state.
        /// </summary>
        Cancelled,

        /// <summary>The load fails for a reason that says nothing about the files.</summary>
        Fails,

        Succeeds,
    }

    private readonly Lock gate = new();
    private readonly Queue<Outcome> script;
    private readonly Outcome fallback;
    private int attempts;
    private int warmUps;

    public ScriptedLoadEngine(IEnumerable<Outcome> script, Outcome fallback = Outcome.Succeeds)
    {
        this.script = new Queue<Outcome>(script);
        this.fallback = fallback;
    }

    public int Attempts => Volatile.Read(ref attempts);

    public int WarmUps => Volatile.Read(ref warmUps);

    public Task LoadModels(string directory)
    {
        Interlocked.Increment(ref attempts);
        Outcome next;
        lock (gate) { next = script.Count > 0 ? script.Dequeue() : fallback; }
        return next switch
        {
            Outcome.Cancelled => Task.FromException(new OperationCanceledException()),
            // A dead pipe, a worker that died - anything that leaves the verified files on disk
            // untouched and only costs one attempt.
            Outcome.Fails => Task.FromException(new IOException("the engine process went away")),
            _ => Task.CompletedTask,
        };
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        Interlocked.Increment(ref warmUps);
        return Task.FromResult(new ASRResult(string.Empty, TimeSpan.Zero, TimeSpan.Zero));
    }

    public Task Unload() => Task.CompletedTask;
}

/// <summary>
/// An engine whose first load lasts as long as the test wants it to.
/// </summary>
/// <remarks>
/// The one cost a first run cannot avoid is the one-time load, and no other fake spends it: they
/// all answer instantly, so every test so far measured a first run in which the engine was ready
/// before the person had finished reading the screen.
/// </remarks>
public sealed class SlowFirstLoadEngine : IASREngineAdapting
{
    private readonly TaskCompletionSource loadFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int holdFromLoad;
    private int loads;

    /// <param name="holdFromLoad">Which load, 1-based, is the one held open; earlier ones answer at once.</param>
    public SlowFirstLoadEngine(int holdFromLoad = 1)
    {
        this.holdFromLoad = holdFromLoad;
    }
    private int unloads;
    private int warmUps;

    public int Loads => Volatile.Read(ref loads);

    public int Unloads => Volatile.Read(ref unloads);

    public int WarmUps => Volatile.Read(ref warmUps);

    public bool LoadStarted => Loads > 0;

    /// <summary>Let the load through, exactly as the machine does when it is done.</summary>
    public void FinishLoading() => loadFinished.TrySetResult();

    public async Task LoadModels(string directory)
    {
        if (Interlocked.Increment(ref loads) < holdFromLoad) { return; }
        await loadFinished.Task.ConfigureAwait(false);
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        Interlocked.Increment(ref warmUps);
        return Task.FromResult(new ASRResult(string.Empty, TimeSpan.Zero, TimeSpan.Zero));
    }

    public Task Unload()
    {
        Interlocked.Increment(ref unloads);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The dummy application environment.
///
/// Assembled in one place so that different sets of checks do not drift apart in how the
/// application sees the computer.
/// </summary>
public sealed class AppHarness : IDisposable
{
    /// <summary>
    /// Weak on purpose: the "everything is released after Dispose" test measures exactly this
    /// object, and a tidy-up list that held it strongly would make the test pass on the harness
    /// rather than on the code.
    /// </summary>
    private readonly List<WeakReference<AppState>> built = [];

    public AppHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), $"openramble-harness-{Guid.NewGuid()}");
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Settings = SettingsStore.Under(Paths);
        MicrophoneConsent = new FakeMicrophoneConsentFlow(Permissions);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public FakePermissions Permissions { get; } = new();

    public FakeMicrophoneConsentFlow MicrophoneConsent { get; }

    public FakeHotkeyMonitor Monitor { get; } = new();

    public FakeShortcutMonitor CopyMonitor { get; } = new();

    public FakeInputDevices InputDevices { get; } = new();

    /// <summary>
    /// The monotonic clock the silence rule reads. A test advances it by hand; production reads
    /// the stopwatch.
    /// </summary>
    public MonotonicInstant MonotonicNow { get; set; } = MonotonicInstant.Now;

    /// <summary>Where a state built here writes its plain-text log.</summary>
    public string MakeStateLogDirectory() => DictationLogFile.DirectoryUnder(Paths);

    public FakeOverlay Overlay { get; } = new();

    public FakeCapture Capture { get; } = new();

    /// <summary>
    /// The live-level callback the state handed to the capture; a test feeds it. Kept alive
    /// exactly as long as the state that owns it, and no longer: the callback closes over the
    /// state, and a harness that rooted it would fail the "everything is released after Dispose"
    /// test on the harness rather than on the code.
    /// </summary>
    public Action<float[]>? OnSamples =>
        onSamples is { } weak && weak.TryGetTarget(out Action<float[]>? callback) ? callback : null;

    private WeakReference<Action<float[]>>? onSamples;
    private readonly ConditionalWeakTable<object, Action<float[]>> onSamplesLifetime = new();

    public FakeInserter Inserter { get; } = new();

    public FakeSystemEvents SystemEvents { get; } = new();

    public FakeFocusedField FocusedField { get; } = new();

    public FakeLaunchAtLogin LaunchAtLogin { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeFolders Folders { get; } = new();

    public FakeSounds Sounds { get; } = new();

    public BlockingModelDownloader Downloader { get; } = new();

    public FakeTranscription Transcription { get; } = new();

    /// <summary>
    /// The engine used for warm-up. Routine checks do not need it: without it the warm-up counts
    /// as passed. It is set where the warm-up itself is under test.
    /// </summary>
    public IASREngineAdapting? WarmUpEngine { get; set; }

    /// <summary>Zero - permission polling is off: in a test we change them, not the system.</summary>
    public TimeSpan PermissionPollInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Shortens the idle-unload countdown; the policy still decides whether there is one.</summary>
    public TimeSpan? IdleUnloadDelayOverride { get; set; }

    /// <summary>Shortens the retry ladder so a suite never sleeps production-scale intervals.</summary>
    public TimeSpan? EngineWarmupRetryDelay { get; set; }

    public TimeSpan? PrepareDeadline { get; set; }

    public bool LayoutHasAltGraph { get; set; }

    /// <summary>
    /// The platform sentence appended to a hotkey warning. <c>null</c> by default, because the
    /// fake environment is on no platform in particular and the shared half of the warning is
    /// what nearly every test is about.
    /// </summary>
    public string? HotkeySettingsHint { get; set; }

    /// <summary>
    /// The platform's microphone sentences. Windows by default, unlike the hotkey hint above: the
    /// scenarios were ported against the Windows copy and go on pinning it, and a test about the
    /// other platform says so by assigning this.
    /// </summary>
    public MicrophoneRemedy MicrophoneRemedy { get; set; } = MicrophoneRemedy.Windows;

    public AppState MakeState()
    {
        var state = new AppState(new AppEnvironment
        {
            Paths = Paths,
            Settings = Settings,
            Permissions = Permissions,
            HotkeyMonitor = Monitor,
            CopyShortcutMonitor = CopyMonitor,
            InputDevices = InputDevices,
            MonotonicNow = () => MonotonicNow,
            Inserter = Inserter,
            Overlay = Overlay,
            Sounds = Sounds,
            MakeCapture = (_, _, samples, _) =>
            {
                onSamples = new WeakReference<Action<float[]>>(samples);
                if (samples.Target is { } owner) { onSamplesLifetime.AddOrUpdate(owner, samples); }
                return Capture;
            },
            Transcribe = _ => async (_, token) =>
            {
                if (Transcription.Delay is TimeSpan delay)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                if (Transcription.Error is Exception error) { throw error; }
                return new ASRResult(Transcription.Text, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.05));
            },
            PermissionPollInterval = PermissionPollInterval,
            IdleUnloadDelayOverride = IdleUnloadDelayOverride,
            EngineWarmupRetryDelay = EngineWarmupRetryDelay,
            PrepareDeadline = PrepareDeadline,
            ModelDownloader = Downloader,
            MicrophoneConsent = MicrophoneConsent,
            SystemEvents = SystemEvents,
            LocalTranscriber = WarmUpEngine is null ? null : new LocalTranscriber(WarmUpEngine),
            FocusedFieldReader = FocusedField,
            LaunchAtLogin = LaunchAtLogin,
            Clipboard = Clipboard,
            Folders = Folders,
            LayoutHasAltGraph = () => LayoutHasAltGraph,
            HotkeySettingsHint = HotkeySettingsHint,
            MicrophoneRemedy = MicrophoneRemedy,
        });

        built.Add(new WeakReference<AppState>(state));
        return state;
    }

    /// <summary>
    /// Lay out the test inventory and the finished model's marker.
    ///
    /// Sparse files: the logical size matches the manifest, but 670 MB of blocks never touch the
    /// disk. The marker records the same sizes and modification times a production installation
    /// writes after its SHA check, so the store's fast path accepts it without hashing anything.
    /// </summary>
    public void InstallModelMarker()
    {
        ModelManifest manifest = ModelManifest.Bundled();
        var layout = new ModelInstallLayout(manifest, Paths.Models());
        Directory.CreateDirectory(layout.EngineDirectory);

        var installed = new List<ModelReadyMarker.InstalledFile>();
        foreach (ModelManifest.File file in manifest.Files)
        {
            string path = layout.Destination(file, layout.EngineDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(file.ByteCount);
            }

            installed.Add(new ModelReadyMarker.InstalledFile(
                file.Path,
                file.ByteCount,
                new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero)));
        }

        var marker = new ModelReadyMarker(
            manifest,
            DateTimeOffset.UtcNow,
            [.. installed.OrderBy(file => file.Path, StringComparer.Ordinal)]);
        File.WriteAllBytes(layout.ReadyMarker, JsonSerializer.SerializeToUtf8Bytes(marker));
    }

    /// <summary>WAVs quietly kept for safety after failures and interruptions.</summary>
    public IReadOnlyList<string> RecoveryFiles()
    {
        try
        {
            return Directory.GetFiles(Paths.AudioRecovery());
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        foreach (WeakReference<AppState> reference in built)
        {
            if (reference.TryGetTarget(out AppState? state)) { state.Dispose(); }
        }
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
