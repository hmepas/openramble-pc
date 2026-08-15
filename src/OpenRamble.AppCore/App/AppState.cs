using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// All application state in one place.
///
/// Links the hotkey, audio capture, recognition and insertion. The dictation logic itself lives
/// in <see cref="DictationController"/>; what is here is the wiring of the system edges and what
/// the interface sees.
/// </summary>
/// <remarks>
/// <para>
/// The macOS original is <c>@MainActor</c> and gets its serialization from the actor. Here one
/// monitor does the same job: every field is read and written under <c>gate</c>, and the
/// long-running work happens outside it. WPF binding marshals <see cref="PropertyChanged"/> to
/// its dispatcher itself, so no view has to know that a controller callback arrived on a
/// thread-pool thread.
/// </para>
/// <para>
/// <b>Not ported, with reasons.</b> The entire Accessibility permission apparatus - request,
/// relaunch, TCC reset, the "restart required" and "entry needs repair" states - has no Windows
/// counterpart: a keyboard hook and synthetic input need no consent, so there is no grant to
/// lose and no stale record to repair. The updater is gone by decision. The acoustic term
/// hinter's second model, its rebuild-on-dictionary-edit path and its preparation phase are
/// unreachable because no ONNX export exists (see <see cref="ModelPairState"/>); the vocabulary
/// state machine is kept, the calls into it are not.
/// </para>
/// </remarks>
public sealed partial class AppState : INotifyPropertyChanged, IDisposable
{
    private readonly object gate = new();

    private readonly AppPaths paths;
    private readonly SettingsStore settingsStore;
    private readonly IPermissionReading permissions;
    private readonly IHotkeyMonitoring hotkeyMonitor;
    private readonly ITextInserting inserter;
    private readonly IOverlayPresenting overlay;
    private readonly TimeSpan permissionPollInterval;
    private readonly IModelDownloading modelDownloader;
    private readonly IMicrophoneConsentFlow microphoneConsent;
    private readonly ISystemEventSource systemEvents;
    private readonly ILaunchAtLoginService? launchAtLoginService;
    private readonly IHostOnlyClipboard? clipboard;
    private readonly IFolderOpening? folders;
    private readonly Func<bool> layoutHasAltGraph;
    private readonly Func<DateTimeOffset> now;
    private readonly ReplacementsStore replacementsStore;
    private readonly EditLearningWatcher editWatcher;
    private readonly LocalTranscriber? transcriber;
    private readonly string? bundledModelDirectory;

    private AppSettings settings;
    private DictationController? controller;
    private RecordingRecoveryStore? recordingRecovery;
    private ModelStore? store;
    private long mainModelBytes;

    private bool didCompleteInitialPermissionRefresh;
    private bool isRecoveryOperationActive;
    private DictationNotice? noticeAfterSession;
    private bool disposed;

    /// <summary>The loudest sample of the running take, for <see cref="MicrophoneLevelHint"/>.</summary>
    private float takePeak;
    private bool microphoneLevelHintShown;

    public AppState(AppEnvironment environment)
    {
        paths = environment.Paths;
        settingsStore = environment.Settings;
        bundledModelDirectory = environment.BundledModelDirectory;
        permissions = environment.Permissions;
        hotkeyMonitor = environment.HotkeyMonitor;
        inserter = environment.Inserter;
        overlay = environment.Overlay;
        permissionPollInterval = environment.PermissionPollInterval;
        modelDownloader = environment.ModelDownloader;
        microphoneConsent = environment.MicrophoneConsent;
        systemEvents = environment.SystemEvents;
        launchAtLoginService = environment.LaunchAtLogin;
        clipboard = environment.Clipboard;
        folders = environment.Folders;
        layoutHasAltGraph = environment.LayoutHasAltGraph ?? (static () => false);
        now = environment.Now ?? (static () => DateTimeOffset.Now);
        transcriber = environment.LocalTranscriber;
        editWatcher = new EditLearningWatcher(
            environment.FocusedFieldReader ?? new NoFocusedField());

        settings = settingsStore.Read();
        // The saved code is checked against the engine's list. That list is a property of the
        // library and may get narrower when it is updated; without the check every dictation
        // would fail on "unsupported language hint", WAVs would pile up in recovery, and the
        // picker would show a blank row. An unfamiliar code means automatic detection, exactly
        // as it was before the person chose anything.
        recognitionLanguage = RecognitionLanguages.IsSupported(settings.RecognitionLanguage)
            ? settings.RecognitionLanguage
            : null;
        learnFromEdits = settings.LearnFromEdits;
        hotkey = ParseHotkey(settings.Hotkey);
        soundsEnabled = settings.SoundsEnabled;
        launchAtLogin = launchAtLoginService?.IsEnabled ?? false;
        // Test environments substitute a ready-made ASR closure and need no engine warm-up.
        // Production always passes a shared transcriber.
        isEngineReady = transcriber is null;

        replacementsStore = new ReplacementsStore(Path.Combine(paths.Support(), "replacements.json"));
        ReplacementsStore.Load loaded = replacementsStore.Read();
        replacements = loaded.Replacements;
        dictionaryProblem = loaded.Problem;

        SetUp(environment);

        // Reported after assembly: before it there would have been nowhere to show the message.
        if (loaded.Problem is ReplacementsStore.Problem problem)
        {
            Notify(new DictationNotice(DictationNoticeKind.Warning, problem.Message));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // MARK: - Assembly

    private void SetUp(AppEnvironment environment)
    {
        try
        {
            IAudioCapturing capture = environment.MakeCapture(
                paths.Takes(),
                error =>
                    // The audio stream is no longer usable in the middle of a sentence. Waiting
                    // for a stop is not an option: the person is speaking into emptiness and the
                    // reason has to be shown exactly.
                    controller?.Interrupt(CaptureFailureMessage(error)),
                samples =>
                {
                    // The peak is enough for the waveform; RMS would hide the short consonant
                    // transients that make the live signal feel responsive.
                    float peak = 0;
                    foreach (float sample in samples)
                    {
                        peak = Math.Max(peak, Math.Abs(sample));
                    }

                    lock (gate) { takePeak = Math.Max(takePeak, peak); }
                    OnInputLevel?.Invoke(peak);
                });

            recordingRecovery = new RecordingRecoveryStore(paths.AudioRecovery());

            ModelManifest manifest = ModelManifest.Bundled();
            var layout = new ModelInstallLayout(manifest, paths.Models());
            store = new ModelStore(manifest, layout, modelDownloader);
            mainModelBytes = manifest.TotalByteCount;
            engineDirectory = layout.EngineDirectory;

            Func<string?> languageHint = () => RecognitionLanguage;
            Func<float[], CancellationToken, Task<ASRResult>>? transcribeSamples = transcriber is null
                ? null
                : async (samples, token) =>
                {
                    // The path every dictation actually takes: the file path in the composition
                    // root logs its own transcribe.* events, and this one said nothing - which
                    // left "Nothing was recognized" with no line about what the engine was
                    // handed or how it answered. Counts and durations only, never the words.
                    int session = DiagnosticLog.Session;
                    long began = Stopwatch.GetTimestamp();
                    DiagnosticLog.Emit(
                        "transcribe.begin",
                        ("session", session),
                        ("language", languageHint() ?? "auto"),
                        ("audio_ms", samples.Length / 16),
                        ("peak_dbfs", MicrophoneLevelHint.Decibels(InputGain.Peak(samples))));
                    try
                    {
                        ASRResult result = await transcriber.Transcribe(samples, languageHint(), token).ConfigureAwait(false);
                        DiagnosticLog.Emit(
                            "transcribe.end",
                            ("session", session),
                            ("ms", Stopwatch.GetElapsedTime(began)),
                            ("chars", result.Text.Length),
                            ("words", result.Words.Count));
                        return result;
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.EmitException(
                            "transcribe.failed",
                            error,
                            ("session", session),
                            ("ms", Stopwatch.GetElapsedTime(began)));
                        throw;
                    }
                };

            var built = new DictationController(
                capture,
                environment.Transcribe(layout.EngineDirectory, languageHint),
                inserter,
                overlay,
                new GatedSounds(environment.Sounds, () => SoundsEnabled),
                transcribeSamples,
                recordingRecovery,
                MakePipeline,
                prepareForTranscription: transcriber is null
                    ? null
                    : token => transcriber.Prepare(layout.EngineDirectory));

            built.OnStateChange = state =>
            {
                // The first state of a session is the first thing anyone hears about it, and it
                // arrives before the microphone is asked for anything. Publishing the number here
                // is what lets the capture, the engine and the inserter write their own events
                // against the same session.
                DiagnosticLog.Session = built.CurrentSession;
                DiagnosticLog.Emit(
                    "dictation.state",
                    ("session", DiagnosticLog.Session),
                    ("to", state),
                    ("hands_free", IsHandsFreeActive));
                DictationStateValue = state;
                FlushNoticeAfterSession(state);
                // The person just pressed the key and is about to speak for seconds: if the
                // engine sat idle long enough for the system to reclaim it, reload it now, under
                // their voice, instead of after it.
                if (state == DictationState.Preparing)
                {
                    lock (gate) { takePeak = 0f; }
                    WarmEngineUnderVoiceIfCold();
                }

                if (state == DictationState.Transcribing) { JudgeMicrophoneLevel(); }

                // A finished session is a residency event: the engine just became safely idle,
                // and a deferred unload may now proceed.
                if (state == DictationState.Idle) { EvaluateResidency(); }
            };
            built.OnNotice = notice =>
            {
                EmitNotice("dictation.notice", notice, DiagnosticLog.Session);
                LastNotice = notice;
                if (notice.RecoverableText is string text)
                {
                    RecoveredText = text;
                    // The words also join Recent Dictations at failure time: the single recovery
                    // slot is overwritten by the next failure, and without this copy the first
                    // failure's words would silently vanish from the person's reach.
                    RememberFailedInsertion(text);
                }

                // The kernel explained itself. Our own explanation on top of its words would be
                // worse than silence: a session has one reason for ending, not two. The one
                // exception is an Info - "Nothing was recognized" - when the explanation waiting
                // is the microphone level: that IS the reason, and the person needs it more than
                // the symptom.
                if (notice.Kind != DictationNoticeKind.Info)
                {
                    lock (gate) { noticeAfterSession = null; }
                }
            };
            built.OnHandsFreeChange = active =>
            {
                // The monitor needs to know the mode: in it a single press means "stop", not
                // "start a new dictation".
                hotkeyMonitor.IsHandsFreeActive = active;
                IsHandsFreeActive = active;
            };
            built.OnTranscriptionStall = RecycleWedgedEngine;
            built.OnTextInserted = text =>
            {
                RecordSuccessfulDictation(text);
                if (!LearnFromEdits) { return; }
                editWatcher.BeginWatching(text, (original, edited) => Learn(original, edited));
            };
            // Kept for diagnostics and performance tests; deliberately not shown over the
            // destination app after the text has already arrived.
            built.OnSpeed = report =>
            {
                // The stage durations of one dictation, and the only place they exist: release to
                // text, release to paste, release to a restored clipboard, and how long the
                // microphone took to wake up.
                DiagnosticLog.Emit(
                    "dictation.speed",
                    ("session", DiagnosticLog.Session),
                    ("recognized_ms", report.ToRecognizedText),
                    ("pasted_ms", report.ToPasteDispatched),
                    ("restored_ms", report.ToClipboardRestored),
                    ("mic_startup_ms", report.MicrophoneStartup));
                LastSpeed = report;
            };
            built.OnDictationCompleted = provenance =>
            {
                LastDictationValue = new LastDictation(provenance.FinalText, provenance);
                // Real inference just ran — the cold-suspicion clock restarts.
                lock (gate) { lastEngineActivity = now(); }
            };

            controller = built;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ModelManifestError or ModelInstallError)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                $"Couldn't prepare the app's working folders: {error.Message}"));
        }

        WireHotkey();
        ObserveSystemEvents();
        // Unlike macOS, the hook needs no permission - it starts once and stays for the life of
        // the process. Nothing about a revoked microphone makes the keyboard hook invalid.
        hotkeyMonitor.Start();
        RefreshPermissions();

        _ = Task.Run(async () =>
        {
            await ImportAbandonedRecordings().ConfigureAwait(false);
            await RefreshModelState().ConfigureAwait(false);
            if (ModelStateValue.IsReady)
            {
                await WarmUpEngine().ConfigureAwait(false);
            }
            else if (HasBundledModel)
            {
                // The model came with the installer. Nobody should have to press a button, let
                // alone wait for a download, to get what is already on their disk: put it in
                // place now, before the setup window even reaches the question.
                InstallModel();
            }
        });
    }

    /// <summary>
    /// Peak level from the microphone - feeds the waveform in the panel.
    ///
    /// There is deliberately no "no sound detected" watcher: pausing to think before a phrase is
    /// normal speech, not an error, and a hint built on a two-second timer fired on every such
    /// pause. A dead input is still honestly visible - by the flat waveform during recording and
    /// by the final "Nothing was recognized" at the end.
    /// </summary>
    public Action<float>? OnInputLevel { get; set; }

    /// <summary>
    /// Tidy up recordings left behind by a kill or power loss — quietly.
    ///
    /// The import repairs interrupted WAV headers, discards fragments too short to hold speech,
    /// and prunes the folder by age, count and size. Nothing is announced and no menu item
    /// appears: a broken last dictation is a rare technical event that already surfaced the
    /// moment it happened.
    /// </summary>
    private async Task ImportAbandonedRecordings()
    {
        if (recordingRecovery is null) { return; }
        try
        {
            _ = await recordingRecovery.ImportAbandoned(paths.Takes()).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                $"Couldn't prepare recording recovery: {error.Message}"));
        }
    }

    public static string CaptureFailureMessage(AudioCaptureError error) => error switch
    {
        AudioCaptureError.UnsupportedAudioFormat format =>
            $"Couldn't handle the selected microphone's audio format: {format.Reason}",
        AudioCaptureError.MicrophonePermissionDenied =>
            "No microphone access. Open Windows Settings.",
        AudioCaptureError.EngineUnavailable engine =>
            $"The microphone stopped responding: {engine.Reason}",
        AudioCaptureError.DiskFull => "Couldn't record audio: no free disk space.",
        AudioCaptureError.WriteFailed write => $"Couldn't record audio: {write.Reason}",
        AudioCaptureError.NotRecording => "Recording stopped unexpectedly.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    // MARK: - Hotkey

    private void WireHotkey()
    {
        hotkeyMonitor.SetHotkey(Hotkey);
        hotkeyMonitor.OnPress = BeginHoldToDictate;
        hotkeyMonitor.OnRelease = EndHoldToDictate;
        hotkeyMonitor.OnDoubleTap = () =>
        {
            switch (DictationStateValue)
            {
                case DictationState.Preparing:
                case DictationState.Listening:
                    // The session is already running - the first press started it. Promote it to
                    // hands-free instead of starting a new one: a new one would not have started
                    // anyway, because this one has not finished.
                    controller?.PromoteToHandsFree();
                    break;

                case DictationState.Idle:
                    if (!ExplainIfNotReady()) { return; }
                    controller?.Begin(handsFree: true, isEnabled: IsDictationReady, isModelReady: ModelStateValue.IsReady);
                    break;

                case DictationState.Transcribing:
                case DictationState.Inserting:
                    break;
            }
        };
        // The raw edges, alongside the gestures and never instead of them. They carry no policy:
        // whether a press starts anything is decided above, and this pair only records that the
        // key arrived - which is the one thing the first-launch try-out has to be able to show.
        hotkeyMonitor.OnHotkeyDown = () =>
        {
            HotkeyPressCount += 1;
            IsHotkeyHeld = true;
        };
        hotkeyMonitor.OnHotkeyUp = () => IsHotkeyHeld = false;
        hotkeyMonitor.OnTrackingFailure = reason =>
        {
            // Kept, not just announced. A panel lives four seconds; a hook that never installed
            // lives for the rest of the process, and the first-launch try-out - the one screen
            // whose entire job is to answer "does the key reach OpenRamble" - has no other way to
            // tell a dead hook from a person who has not pressed anything yet.
            HotkeyTrackingFailure = reason;
            Notify(new DictationNotice(DictationNoticeKind.Failure, reason));
        };
        hotkeyMonitor.OnSingleTapWhileHandsFree = () => controller?.StopHandsFree();
        // The hold turned out to be a shortcut over the chosen key: the recording ends quietly -
        // no insertion, no message, no error sound. The person pressed a shortcut, they did not
        // dictate.
        hotkeyMonitor.OnAbortShortcut = () => controller?.Cancel();
        // Escape cancels only a dictation in progress. The rest of the time it is an ordinary
        // key and is not intercepted.
        hotkeyMonitor.OnEscape = CancelCurrentDictation;
    }

    /// <summary>
    /// The dictation key went down. The only place a hold-to-dictate gesture may start from.
    /// </summary>
    private void BeginHoldToDictate()
    {
        if (IsRecoveryOperationActive)
        {
            DiagnosticLog.Emit(
                "dictation.begin",
                ("session", DiagnosticLog.Session),
                ("started", false),
                ("refused", "recovery"));
            return;
        }

        if (!ExplainIfNotReady())
        {
            DiagnosticLog.Emit(
                "dictation.begin",
                ("session", DiagnosticLog.Session),
                ("started", false),
                ("refused", "not-ready"));
            return;
        }

        // The state the press arrived in, read before it is acted on: it is the reason a press
        // that started nothing started nothing.
        DictationState before = DictationStateValue;
        int previousSession = controller?.CurrentSession ?? 0;
        controller?.Begin(handsFree: false, isEnabled: IsDictationReady, isModelReady: ModelStateValue.IsReady);

        // The policy each session is started under, so a session that behaves differently from
        // the last one can be told apart from a session that was started differently. Written
        // after the call and not before it: the kernel refuses a press that lands on a session
        // still finishing, and a line claiming "started" for one of those - a phantom key event
        // arriving mid-transcription is a virtual machine's daily bread - sent a reader looking
        // for a recording that never existed.
        int session = controller?.CurrentSession ?? 0;
        DiagnosticLog.Emit(
            "dictation.begin",
            ("session", session),
            ("started", session != previousSession),
            ("hands_free", false),
            ("enabled", IsDictationReady),
            ("model_ready", ModelStateValue.IsReady),
            ("state", before));
    }

    /// <summary>The dictation key came back up.</summary>
    private void EndHoldToDictate()
    {
        DiagnosticLog.Emit(
            "dictation.stop",
            ("session", DiagnosticLog.Session),
            ("state", DictationStateValue));
        controller?.Stop();
    }

    /// <summary>
    /// One notice, as the log may carry it.
    /// </summary>
    /// <remarks>
    /// The message is written because every one of them is a fixed template from this
    /// repository - which is the whole point of grepping for it - and because the recognized
    /// text a failure hands back travels in <c>RecoverableText</c>, which is not written here and
    /// must never be.
    /// </remarks>
    /// <param name="session">
    /// The dictation it ends, or <c>null</c> for the notices that belong to no dictation at all -
    /// a failed download, a hook the system refused. Those read <c>session=none</c>, which is the
    /// true answer rather than the number of whatever was dictated last.
    /// </param>
    private static void EmitNotice(string name, DictationNotice notice, int? session = null) =>
        DiagnosticLog.Emit(
            name,
            ("session", session),
            ("kind", notice.Kind),
            ("words_lost", notice.WordsDidNotLand),
            ("recoverable_chars", notice.RecoverableText?.Length),
            ("kept_audio", notice.RecoveryAudio is not null),
            ("message", notice.Message));

    /// <summary>
    /// Start a dictation from an on-screen press-and-hold control instead of the key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a second way to dictate - a second way to press the same key.</b> This calls the
    /// very method the hotkey monitor calls, so the readiness explanation, the session policy and
    /// every completion path are the ones the key already uses. A parallel "start dictation"
    /// entry point would be a second machine that agrees with the first only until one of them is
    /// changed.
    /// </para>
    /// <para>
    /// It exists because on Windows the key can be eaten before the application ever sees it - a
    /// virtual machine client, a remote desktop, an elevated window - and until the person knows
    /// that, they cannot tell a broken keyboard path from a broken product. A button that runs
    /// the whole record → recognize → insert chain settles it in one hold.
    /// </para>
    /// <para>
    /// <see cref="IsHotkeyHeld"/> and <see cref="HotkeyPressCount"/> are deliberately left alone:
    /// they are the try-out's evidence about the key, and a mouse press is not evidence about the
    /// key.
    /// </para>
    /// </remarks>
    public void PressDictationButton()
    {
        if (IsDictationButtonHeld) { return; }
        IsDictationButtonHeld = true;
        BeginHoldToDictate();
    }

    /// <summary>
    /// The on-screen control was released - the same thing as letting the key go.
    /// </summary>
    /// <remarks>
    /// Safe to call without a matching press: the mouse can leave the button, the window can lose
    /// capture, and both of those have to end the hold. A stop with nothing running is ignored by
    /// the kernel, which is what makes calling it twice harmless.
    /// </remarks>
    public void ReleaseDictationButton()
    {
        if (!IsDictationButtonHeld) { return; }
        IsDictationButtonHeld = false;
        EndHoldToDictate();
    }

    /// <summary>
    /// Can dictation start; if not, say why.
    ///
    /// The kernel rejects the start silently, and rightly so: it has neither a screen nor words.
    /// But from the outside, silence in response to a press is indistinguishable from a broken
    /// application - especially in the warm-up window after installation, where everything is
    /// granted, everything is downloaded, and the key still does nothing for tens of seconds.
    ///
    /// Asked only at rest. A press in the middle of a running dictation is also rejected, but
    /// there silence is appropriate: the panel is on screen and the person can see what is
    /// happening.
    /// </summary>
    private bool ExplainIfNotReady()
    {
        if (DictationStateValue != DictationState.Idle) { return true; }

        string? reason = DictationReadiness.Reason(
            MicrophoneGranted,
            ModelStateValue,
            IsEngineReady,
            HasEngineBeenReady);
        if (reason is null) { return true; }

        // Warning precisely: for a screen reader this is an urgent announcement, and the person
        // just pressed a key and expects an answer now, not after the synthesizer finishes
        // reading somebody else's sentence.
        Notify(new DictationNotice(DictationNoticeKind.Warning, reason));
        return false;
    }

    /// <summary>Visible button in the menu: the user is not required to remember the gesture.</summary>
    public void FinishCurrentDictation()
    {
        if (IsRecoveryOperationActive) { return; }
        if (DictationStateValue is not (DictationState.Preparing or DictationState.Listening)) { return; }
        StopCurrentRecording();
    }

    /// <summary>
    /// The general safe cancel, for Escape and for the menu.
    ///
    /// The message is given only for a cancellation that actually happened. From
    /// <see cref="DictationState.Inserting"/> there is nothing to cancel - the paste has already
    /// gone into someone else's window - and the kernel ignores such a request. "The recording
    /// was deleted" would then say the exact opposite of what the person sees a moment later in
    /// their document, and would also erase the real warning about the insertion.
    /// </summary>
    public void CancelCurrentDictation()
    {
        if (IsRecoveryOperationActive) { return; }
        if (!DictationStopPolicy.CanCancel(DictationStateValue)) { return; }
        controller?.Cancel();
        Notify(new DictationNotice(DictationNoticeKind.Info, "Dictation cancelled. The recording was deleted."));
    }

    /// <summary>
    /// Finish a running recording the way a person would.
    ///
    /// In hands-free mode releasing the key means nothing, and an ordinary stop would be ignored -
    /// the recording would keep running into nowhere.
    /// </summary>
    private void StopCurrentRecording()
    {
        if (IsHandsFreeActive) { controller?.StopHandsFree(); }
        else { controller?.Stop(); }
    }

    // MARK: - System events

    private void ObserveSystemEvents()
    {
        systemEvents.WillSleep += HandleSleep;
        systemEvents.DidWake += HandleWake;
        systemEvents.AudioDeviceChanged += HandleAudioConfigurationChange;
        systemEvents.ApplicationBecameActive += HandleApplicationBecameActive;
        systemEvents.MemoryPressureChanged += RegisterMemoryPressure;
    }

    private void HandleApplicationBecameActive() => RefreshPermissions();

    /// <summary>
    /// The computer goes to sleep.
    ///
    /// A key release that happens during sleep never reaches us: the system does not send events
    /// to a sleeping machine. Without stopping, the session would stay in "listening" forever -
    /// microphone on, recording indicator lit - and the only way out would be Escape.
    /// </summary>
    private void HandleSleep()
    {
        switch (DictationStateValue)
        {
            case DictationState.Listening:
                // What was said before sleep is already recorded. Recognize it, do not throw it
                // away.
                lock (gate)
                {
                    noticeAfterSession = new DictationNotice(
                        DictationNoticeKind.Info,
                        "The PC went to sleep — recording had to stop.");
                }

                StopCurrentRecording();
                break;

            case DictationState.Preparing:
                // Nothing has been recorded yet - there is nothing to lose.
                controller?.Cancel();
                break;

            case DictationState.Idle:
            case DictationState.Transcribing:
            case DictationState.Inserting:
                break;
        }
    }

    /// <summary>
    /// The computer woke up.
    ///
    /// The key was released during sleep but no event said so: the monitor still considers it
    /// held and would swallow the next press. Restarting the tracking is the only thing that
    /// erases the memory of a started gesture.
    /// </summary>
    private void HandleWake()
    {
        hotkeyMonitor.Stop();
        // The release never arrived, so nothing will ever say the key came up. Restarting
        // tracking erases the gesture; this erases the same memory on our side.
        IsHotkeyHeld = false;
        hotkeyMonitor.Start();
        RefreshPermissions();
        RevalidateEngineAfterWake();
        EvaluateResidency();
    }

    /// <summary>
    /// The audio device changed mid-recording.
    ///
    /// The headphones were pulled out, the monitor was switched off - the engine is still
    /// running, but frames no longer reach it. The person speaks into silence and would learn
    /// about it only from an empty result.
    /// </summary>
    private void HandleAudioConfigurationChange()
    {
        if (DictationStateValue != DictationState.Listening) { return; }
        controller?.PreserveActiveRecording(
            "The microphone or audio device was disconnected. Dictation stopped.");
    }

    /// <summary>
    /// The take is closed - was it loud enough to work with? Judged once per run, at the moment
    /// the microphone stops, and said after the session so it does not cover "Transcribing…".
    /// </summary>
    private void JudgeMicrophoneLevel()
    {
        float peak;
        lock (gate)
        {
            peak = takePeak;
            DiagnosticLog.Emit(
                "capture.level",
                ("session", DiagnosticLog.Session),
                ("peak_dbfs", MicrophoneLevelHint.Decibels(peak)));
            if (microphoneLevelHintShown || !MicrophoneLevelHint.IsTooQuiet(peak)) { return; }

            microphoneLevelHintShown = true;
            noticeAfterSession = new DictationNotice(
                DictationNoticeKind.Warning,
                MicrophoneLevelHint.Message(peak));
        }
    }

    /// <summary>
    /// Say what was waiting for the end of the session.
    ///
    /// It cannot be said immediately: right after the stop the kernel redraws the panel under
    /// "transcribing", and the explanation would live on screen for a split second. And it must
    /// be said - otherwise it is unclear why the recording was cut off mid-sentence.
    /// </summary>
    private void FlushNoticeAfterSession(DictationState state)
    {
        DictationNotice? pending;
        lock (gate)
        {
            if (state != DictationState.Idle || noticeAfterSession is null) { return; }
            pending = noticeAfterSession;
            noticeAfterSession = null;
        }

        Notify(pending);
    }

    /// <summary>
    /// Putting the person's own clipboard back happens a second after the paste, detached, with
    /// nobody left to throw to. If it fails they silently lose whatever they had copied and our
    /// dictation stays on the board in its place — exactly the kind of quiet loss the project
    /// forbids. This is the only path that can say so.
    /// </summary>
    public void ReportClipboardRestoreFailure() =>
        Notify(new DictationNotice(
            DictationNoticeKind.Warning,
            "The text was inserted, but the previous clipboard couldn't be restored."));

    /// <summary>
    /// Show the message to the person.
    ///
    /// Through the overlay, not just the <see cref="LastNotice"/> field: the app has no window
    /// it can be sure is on screen, and messages like "dictation is off" were never received at
    /// all.
    /// </summary>
    private void Notify(DictationNotice notice)
    {
        EmitNotice("app.notice", notice);
        LastNotice = notice;
        _ = overlay.PresentNotice(notice);
    }

    // MARK: - Permissions

    public void RefreshPermissions() => ApplyPermissionSnapshot(permissions.MicrophoneGranted);

    /// <summary>
    /// The timer path of <see cref="RefreshPermissions"/>: the read happens off the UI thread.
    /// Polled once a second on the UI thread it would be a standing invitation for a frozen
    /// interface the moment the system answered slowly under load.
    /// </summary>
    private void PollPermissions()
    {
        bool microphone = permissions.MicrophoneGranted;
        ApplyPermissionSnapshot(microphone);
    }

    private void ApplyPermissionSnapshot(bool microphone)
    {
        bool previous = MicrophoneGranted;

        // Guarded assignments: the interface subscribes to these, and an unconditional write
        // here re-rendered the whole tray scene on every poll tick - a permanent one-hertz UI
        // heartbeat at rest.
        if (microphone != MicrophoneGranted) { MicrophoneGranted = microphone; }

        if (microphone)
        {
            if (MicrophoneState != MicrophoneConsentState.Granted)
            {
                MicrophoneState = MicrophoneConsentState.Granted;
            }
        }
        else
        {
            switch (MicrophoneState)
            {
                case MicrophoneConsentState.WaitingForSettings:
                case MicrophoneConsentState.BlockedForDevice:
                    break;
                default:
                    if (MicrophoneState != MicrophoneConsentState.Denied)
                    {
                        MicrophoneState = MicrophoneConsentState.Denied;
                    }

                    break;
            }
        }

        bool firstRefresh;
        lock (gate)
        {
            firstRefresh = !didCompleteInitialPermissionRefresh;
            didCompleteInitialPermissionRefresh = true;
        }

        if (firstRefresh)
        {
            // A first launch already presents the setup window. A tray notice on top of it
            // duplicates the same work and competes for attention.
            if (OnboardingCompleted && !microphone)
            {
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    "Dictation is off: grant Microphone access in Windows Settings."));
            }
        }
        else if (previous && !microphone)
        {
            if (DictationStateValue is DictationState.Preparing or DictationState.Listening)
            {
                controller?.PreserveActiveRecording(
                    "Microphone access was revoked. Dictation stopped; open Windows Settings.");
            }
            else
            {
                Notify(new DictationNotice(
                    DictationNoticeKind.Failure,
                    "Microphone access was revoked. Open Windows Settings."));
            }
        }

        ReschedulePermissionPolling();
    }

    /// <summary>
    /// Pick the polling frequency for the current state of affairs.
    ///
    /// Polling deliberately continues even when everything is granted — see
    /// <see cref="PermissionPollPolicy"/>: a revoke sends no reliable event, and an app whose
    /// microphone access was taken away looks intact while silently not working.
    /// </summary>
    private void ReschedulePermissionPolling()
    {
        TimeSpan interval = PermissionPollPolicy.Interval(MicrophoneGranted, permissionPollInterval);
        if (interval == PermissionPollingInterval) { return; }

        PermissionPollingInterval = interval;
        lock (gate)
        {
            permissionTimer?.Dispose();
            permissionTimer = null;
            if (interval <= TimeSpan.Zero) { return; }

            permissionTimer = new Timer(_ => PollPermissions(), null, interval, interval);
        }
    }

    private Timer? permissionTimer;

    public void RequestMicrophone() => _ = RequestMicrophoneAsync();

    private async Task RequestMicrophoneAsync()
    {
        bool granted = await microphoneConsent.Request().ConfigureAwait(false);
        if (granted)
        {
            // Opening Settings can take the focus away from a window-less app. Bring the setup
            // window back after a successful grant so onboarding continues where it was left.
            microphoneConsent.ActivateApplication();
        }
        else
        {
            MicrophoneState = MicrophoneConsentState.WaitingForSettings;
            microphoneConsent.OpenSettings();
        }

        RefreshPermissions();
    }

    /// <summary>
    /// Show the folder where kept recordings live.
    ///
    /// The failure notice points at this action, so a preserved take is never a file the person
    /// cannot find without a terminal.
    /// </summary>
    public void OpenRecordingsFolder()
    {
        try
        {
            folders?.Open(paths.AudioRecovery());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                "Couldn't open the recordings folder."));
        }
    }

    // MARK: - Housekeeping

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            permissionTimer?.Dispose();
            permissionTimer = null;
            residencyTimer?.Dispose();
            residencyTimer = null;
            preparationTimer?.Dispose();
            preparationTimer = null;
        }

        systemEvents.WillSleep -= HandleSleep;
        systemEvents.DidWake -= HandleWake;
        systemEvents.AudioDeviceChanged -= HandleAudioConfigurationChange;
        systemEvents.ApplicationBecameActive -= HandleApplicationBecameActive;
        systemEvents.MemoryPressureChanged -= RegisterMemoryPressure;
        editWatcher.Cancel();
        hotkeyMonitor.Stop();
        // The monitor outlives us - it owns an OS hook. Leaving our callbacks on it would keep
        // this object, its controller and its capture alive for the rest of the process.
        hotkeyMonitor.OnPress = null;
        hotkeyMonitor.OnRelease = null;
        hotkeyMonitor.OnDoubleTap = null;
        hotkeyMonitor.OnSingleTapWhileHandsFree = null;
        hotkeyMonitor.OnAbortShortcut = null;
        hotkeyMonitor.OnEscape = null;
        hotkeyMonitor.OnHotkeyDown = null;
        hotkeyMonitor.OnHotkeyUp = null;
        hotkeyMonitor.OnTrackingFailure = null;
        OnInputLevel = null;
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static DictationHotkey ParseHotkey(string? stored) =>
        Enum.TryParse(stored, ignoreCase: true, out DictationHotkey parsed)
            && DictationHotkeyInfo.AllCases.Contains(parsed)
            ? parsed
            // Right Ctrl: free on every layout, present on every keyboard.
            : DictationHotkey.RightCtrl;

    private void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (gate)
        {
            updated = change(settings);
            settings = updated;
        }

        try
        {
            settingsStore.Write(updated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The choice stays in memory for this run; saying nothing would let the person
            // believe it survived the restart.
            Notify(new DictationNotice(
                DictationNoticeKind.Warning,
                $"The setting wasn't saved: {error.Message}"));
        }
    }

    /// <summary>The attention sound, muted by the person's own setting.</summary>
    private sealed class GatedSounds : ISounding
    {
        private readonly ISounding inner;
        private readonly Func<bool> enabled;

        public GatedSounds(ISounding inner, Func<bool> enabled)
        {
            this.inner = inner;
            this.enabled = enabled;
        }

        public Task PlayAttention() => enabled() ? inner.PlayAttention() : Task.CompletedTask;
    }

    /// <summary>A reader that reads nothing: the default when no field reader is supplied.</summary>
    private sealed class NoFocusedField : IFocusedFieldReading
    {
        public FocusedFieldHandle? CaptureFocusedField() => null;
    }
}
