using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>Everything the interface is allowed to see, and the settings it may change.</summary>
public sealed partial class AppState
{
    private DictationState dictationState = DictationState.Idle;
    private ModelState modelState = new ModelState.NotInstalled();
    private bool microphoneGranted;
    private MicrophoneConsentState microphoneState = MicrophoneConsentState.Denied;
    private DictationNotice? lastNotice;
    private bool isPreparingEngine;
    private EnginePreparationState enginePreparation =
        EnginePreparationState.Make(EnginePreparationState.Phase.Idle, TimeSpan.Zero);
    private bool isEngineReady;
    private bool hasEngineBeenReady;
    private int remainingDownloadMegabytes = ModelStatus.FullDownloadMegabytes;
    private string? recoveredText;
    private bool isHandsFreeActive;
    private int successfulDictationCount;
    private bool isHotkeyHeld;
    private int hotkeyPressCount;
    private bool isDictationButtonHeld;
    private string? hotkeyTrackingFailure;
    private LastDictation? lastDictation;
    private IReadOnlyList<RecentDictation> recentDictations = [];
    private ReplacementsStore.Problem? dictionaryProblem;
    private IReadOnlyList<DictionaryReplacement> replacements;
    private DictationHotkey hotkey;
    private bool soundsEnabled;
    private bool learnFromEdits;
    private bool launchAtLogin;
    private DictationSpeedReport? lastSpeed;
    private TimeSpan permissionPollingInterval;

    /// <summary>
    /// The last successful dictation, in process memory only. The dictionary learns from edits
    /// made to it; the text never reaches disk.
    /// </summary>
    /// <param name="Provenance">
    /// What the text was after the dictionary and before the cosmetics. One slot, not a history:
    /// this is where learning from edits finds the protected spans - it has no other honest
    /// source, it only sees the inserted text.
    /// </param>
    public sealed record LastDictation(string InsertedText, PipelineProvenance Provenance);

    /// <summary>
    /// A short, in-memory list for quick copying from the menu. Nothing is persisted, so quitting
    /// the app clears the history.
    /// </summary>
    public sealed record RecentDictation(Guid Id, string Text)
    {
        public RecentDictation(string text)
            : this(Guid.NewGuid(), text)
        {
        }

        public string MenuTitle
        {
            get
            {
                string singleLine = string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return singleLine.Length > 56 ? string.Concat(singleLine.AsSpan(0, 55), "…") : singleLine;
            }
        }
    }

    public DictationState DictationStateValue
    {
        get { lock (gate) { return dictationState; } }
        private set => Set(ref dictationState, value);
    }

    public ModelState ModelStateValue
    {
        get { lock (gate) { return modelState; } }
        private set => Set(ref modelState, value);
    }

    public bool MicrophoneGranted
    {
        get { lock (gate) { return microphoneGranted; } }
        private set => Set(ref microphoneGranted, value);
    }

    public MicrophoneConsentState MicrophoneState
    {
        get { lock (gate) { return microphoneState; } }
        private set => Set(ref microphoneState, value);
    }

    public DictationNotice? LastNotice
    {
        get { lock (gate) { return lastNotice; } }
        private set => Set(ref lastNotice, value);
    }

    public bool IsPreparingEngine
    {
        get { lock (gate) { return isPreparingEngine; } }
        private set => Set(ref isPreparingEngine, value);
    }

    /// <summary>
    /// What to show while the engine prepares for the first dictation.
    ///
    /// The seconds are counted here, not in the view: in the view it would show "0 s" for the
    /// whole preparation, because the state arrives once at the beginning and once at the end -
    /// exactly when the counter is no longer needed.
    /// </summary>
    public EnginePreparationState EnginePreparation
    {
        get { lock (gate) { return enginePreparation; } }
        private set => Set(ref enginePreparation, value);
    }

    public bool IsEngineReady
    {
        get { lock (gate) { return isEngineReady; } }
        private set
        {
            if (!Set(ref isEngineReady, value)) { return; }
            if (value) { HasEngineBeenReady = true; }
        }
    }

    /// <summary>
    /// Whether the engine has ever been ready in this process.
    ///
    /// Splits two very different "not ready" states: the FIRST warm-up after install (blocks
    /// dictation with an honest message — there is genuinely nothing to recognize with yet) and a
    /// an idle unload (recording must start instantly; the reload rides under the voice).
    /// </summary>
    public bool HasEngineBeenReady
    {
        get { lock (gate) { return hasEngineBeenReady; } }
        private set => Set(ref hasEngineBeenReady, value);
    }

    /// <summary>How much the install button will download: the full volume, or nothing left.</summary>
    public int RemainingDownloadMegabytes
    {
        get { lock (gate) { return remainingDownloadMegabytes; } }
        private set => Set(ref remainingDownloadMegabytes, value);
    }

    /// <summary>Text of a failed insertion. Never written to disk.</summary>
    public string? RecoveredText
    {
        get { lock (gate) { return recoveredText; } }
        private set => Set(ref recoveredText, value);
    }

    /// <summary>Whether recording continues without holding the key - shown in the menu.</summary>
    public bool IsHandsFreeActive
    {
        get { lock (gate) { return isHandsFreeActive; } }
        private set => Set(ref isHandsFreeActive, value);
    }

    /// <summary>Only a successful insertion counts as a passed try-out in onboarding.</summary>
    public int SuccessfulDictationCount
    {
        get { lock (gate) { return successfulDictationCount; } }
        private set => Set(ref successfulDictationCount, value);
    }

    /// <summary>
    /// Is the dictation key physically down right now?
    /// </summary>
    /// <remarks>
    /// Deliberately not the same question as "is a dictation running". A press that started
    /// nothing still moves this, and that difference is the only honest answer to "is the key
    /// even reaching the app" - the question the first-launch try-out is there to settle.
    /// </remarks>
    public bool IsHotkeyHeld
    {
        get { lock (gate) { return isHotkeyHeld; } }
        private set => Set(ref isHotkeyHeld, value);
    }

    /// <summary>
    /// How many times the dictation key has been pressed since launch.
    /// </summary>
    /// <remarks>
    /// A counter rather than a flag, for the same reason as
    /// <see cref="SuccessfulDictationCount"/>: a window that opens mid-session compares against
    /// the value it saw on opening, so a press from an earlier session cannot pass its step for
    /// it. Nothing about which key, when, or in what order is kept - the privacy invariant leaves
    /// room for a count and for nothing else.
    /// </remarks>
    public int HotkeyPressCount
    {
        get { lock (gate) { return hotkeyPressCount; } }
        private set => Set(ref hotkeyPressCount, value);
    }

    /// <summary>
    /// Is the on-screen press-and-hold control down right now?
    /// </summary>
    /// <remarks>
    /// A separate flag from <see cref="IsHotkeyHeld"/>, and it must stay separate: the try-out
    /// badge is the answer to "does the key reach OpenRamble", and a mouse press proves nothing
    /// about a key. Merging them would make the badge light up for a keyboard that never worked.
    /// </remarks>
    public bool IsDictationButtonHeld
    {
        get { lock (gate) { return isDictationButtonHeld; } }
        private set => Set(ref isDictationButtonHeld, value);
    }

    /// <summary>
    /// Why the dictation key is not being watched, if it is not. <c>null</c> - tracking is up.
    /// </summary>
    /// <remarks>
    /// A hook the system refuses to install is the one failure with no symptom of its own: every
    /// callback simply stays quiet for the life of the process, which looks exactly like a person
    /// who is not pressing anything. The notice that announces it lives four seconds; this
    /// outlives it, so the try-out step can say so for as long as it is true - and no longer:
    /// the first edge that does arrive, whatever road it took, clears it.
    /// </remarks>
    public string? HotkeyTrackingFailure
    {
        get { lock (gate) { return hotkeyTrackingFailure; } }
        private set => Set(ref hotkeyTrackingFailure, value);
    }

    public LastDictation? LastDictationValue
    {
        get { lock (gate) { return lastDictation; } }
        private set => Set(ref lastDictation, value);
    }

    public IReadOnlyList<RecentDictation> RecentDictations
    {
        get { lock (gate) { return recentDictations; } }
        private set => Set(ref recentDictations, value);
    }

    /// <summary>What is wrong with the dictionary. Until <c>null</c>, the dictionary is read-only.</summary>
    public ReplacementsStore.Problem? DictionaryProblem
    {
        get { lock (gate) { return dictionaryProblem; } }
        private set => Set(ref dictionaryProblem, value);
    }

    /// <summary>
    /// The replacement dictionary. Changes only through the methods below: a direct write would
    /// bypass the check that the dictionary can be saved at all.
    /// </summary>
    public IReadOnlyList<DictionaryReplacement> Replacements
    {
        get { lock (gate) { return replacements; } }
        private set => Set(ref replacements, value);
    }

    public DictationHotkey Hotkey
    {
        get { lock (gate) { return hotkey; } }
        set
        {
            if (!Set(ref hotkey, value)) { return; }
            UpdateSettings(current => current with { Hotkey = value.ToString() });
            hotkeyMonitor.SetHotkey(value);
            Raise(nameof(HotkeyWarning));
        }
    }

    public bool SoundsEnabled
    {
        get { lock (gate) { return soundsEnabled; } }
        set
        {
            if (!Set(ref soundsEnabled, value)) { return; }
            UpdateSettings(current => current with { SoundsEnabled = value });
        }
    }

    /// <summary>
    /// Whether to learn from edits of inserted text. Only the field the app itself inserted into
    /// is read, and only in the first half a minute.
    ///
    /// <b>Off by default.</b> This is the only place the application looks into the contents of
    /// someone else's window - and the product's promise is "we read exactly which application is
    /// active: not the screen, not the content". While the switch defaulted to on and was not in
    /// settings, that promise was false and the person had no way to notice. Turning it on is a
    /// deliberate human action, not our default.
    /// </summary>
    public bool LearnFromEdits
    {
        get { lock (gate) { return learnFromEdits; } }
        set
        {
            if (!Set(ref learnFromEdits, value)) { return; }
            UpdateSettings(current => current with { LearnFromEdits = value });
            if (!value) { editWatcher.Cancel(); }
        }
    }

    /// <summary>
    /// Start at login. Default off: an app that installs itself into startup without being asked
    /// has decided something that is not its to decide.
    /// </summary>
    public bool LaunchAtLogin
    {
        get { lock (gate) { return launchAtLogin; } }
        set
        {
            if (!Set(ref launchAtLogin, value)) { return; }
            ApplyLaunchAtLogin(value);
        }
    }

    /// <summary>Last speed measurement. In memory only, numbers only.</summary>
    public DictationSpeedReport? LastSpeed
    {
        get { lock (gate) { return lastSpeed; } }
        private set => Set(ref lastSpeed, value);
    }

    /// <summary>
    /// How often the permission is polled right now. Zero - polling is not running.
    ///
    /// Visible from the outside deliberately: the promise "at rest the application does nothing"
    /// is checked with this very number.
    /// </summary>
    public TimeSpan PermissionPollingInterval
    {
        get { lock (gate) { return permissionPollingInterval; } }
        private set => Set(ref permissionPollingInterval, value);
    }

    public bool IsPollingPermissions { get { lock (gate) { return permissionTimer is not null; } } }

    /// <summary>Is the preparation countdown running? The "the app is silent at rest" test asks this.</summary>
    public bool IsCountingEnginePreparation { get { lock (gate) { return preparationTimer is not null; } } }

    /// <summary>Retry and Delete recovery run one at a time and block the hotkey.</summary>
    public bool IsRecoveryOperationActive { get { lock (gate) { return isRecoveryOperationActive; } } }

    public bool IsDictationReady =>
        MicrophoneGranted && ModelStateValue.IsReady && (IsEngineReady || HasEngineBeenReady);

    /// <summary>
    /// The platform's microphone sentences, for the two screens that draw the permission line.
    ///
    /// Handed on rather than re-read from the platform by each window: the composition root is
    /// where a build says which platform it is, and it says it once.
    /// </summary>
    public MicrophoneRemedy MicrophoneRemedy => microphoneRemedy;

    /// <summary>Warning about the selected key, if any.</summary>
    public string? HotkeyWarning =>
        HotkeyAdvice.Warning(Hotkey, KeyUsageInfo.Of(Hotkey, layoutHasAltGraph()), hotkeySettingsHint);

    /// <summary>
    /// Can the dictionary be changed right now?
    ///
    /// It cannot in exactly one case: the previous dictionary could not be read. Any write would
    /// then erase it whole - and the person would lose everything they accumulated.
    /// </summary>
    public bool IsDictionaryEditable => DictionaryProblem is null;

    /// <summary>
    /// Has the person finished the setup flow - the revision this build asks for, not any older
    /// one? See <see cref="OnboardingRevision"/>.
    /// </summary>
    public bool OnboardingCompleted
    {
        get { lock (gate) { return settings.OnboardingCompletedRevision >= OnboardingRevision.Current; } }
        set
        {
            if (OnboardingCompleted == value) { return; }
            UpdateSettings(current => current with
            {
                OnboardingCompletedRevision = value ? OnboardingRevision.Current : 0,
            });
            Raise();
        }
    }

    /// <summary>
    /// A registration failure has to be visible: leaving a person silently without autostart
    /// brings back "the key does nothing after a reboot".
    /// </summary>
    private void ApplyLaunchAtLogin(bool enabled)
    {
        if (launchAtLoginService is null) { return; }
        try
        {
            launchAtLoginService.SetEnabled(enabled);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            bool actual = launchAtLoginService.IsEnabled;
            if (enabled != actual)
            {
                Set(ref launchAtLogin, actual, nameof(LaunchAtLogin));
            }

            Notify(new DictationNotice(
                DictationNoticeKind.Warning,
                $"Could not update the login item: {error.Message}"));
        }
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        lock (gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
            field = value;
        }

        Raise(property);
        return true;
    }
}
