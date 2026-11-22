using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// The edges of the system from which the application is built.
///
/// Here is exactly what a test may not touch: settings on disk, folders, granted permissions,
/// the microphone, other people's applications and the screen. The application substitutes the
/// real implementations, a test substitutes fakes.
/// </summary>
public sealed class AppEnvironment
{
    public required AppPaths Paths { get; init; }

    public required SettingsStore Settings { get; init; }

    public required IPermissionReading Permissions { get; init; }

    public required IHotkeyMonitoring HotkeyMonitor { get; init; }

    /// <summary>
    /// A second watcher, for the shortcut that copies the last dictation.
    ///
    /// Its own monitor rather than another callback on the first: that one watches a bare
    /// modifier being held, this one watches a chord being pressed. Different events, different
    /// machinery. Optional: a platform without one simply has no copy shortcut.
    /// </summary>
    public IShortcutMonitoring? CopyShortcutMonitor { get; init; }

    /// <summary>
    /// How the platform names keys, for the shortcut shown in the menu. Defaults to the Windows
    /// table, which is a table and not a live layout; a platform passes its own.
    /// </summary>
    public IShortcutKeymap? ShortcutKeymap { get; init; }

    /// <summary>The microphones this machine can hear through. Optional: no picker without one.</summary>
    public IAudioInputDeviceSource? InputDevices { get; init; }

    public required ITextInserting Inserter { get; init; }

    public required IOverlayPresenting Overlay { get; init; }

    /// <summary>
    /// The attention sound. AppState gates it on the person's "sounds" setting, so the edge
    /// itself only has to know what it sounds like.
    /// </summary>
    public required ISounding Sounds { get; init; }

    public required CaptureFactory MakeCapture { get; init; }

    /// <summary>
    /// The model that shipped inside the application, if one did: a folder holding the bundled
    /// manifest's files. When it is there, installing means copying and verifying from it, and
    /// nothing is ever downloaded; when it is not, the manifest's network sources are used.
    /// </summary>
    public string? BundledModelDirectory { get; init; }

    /// <summary>
    /// The model this build recognizes with. <c>null</c> - <see cref="ModelManifest.Bundled"/>,
    /// Parakeet v3 for sherpa-onnx; the Linux build passes Parakeet Ultra for transcribe.cpp.
    /// </summary>
    public ModelManifest? ModelManifest { get; init; }

    /// <summary>
    /// The model <see cref="ModelManifest"/> replaced, if this build still knows it: a machine
    /// that holds it keeps dictating with it until the new one is downloaded, verified and loaded,
    /// and only then is it deleted. See <see cref="ModelLineup"/>.
    /// </summary>
    public ModelManifest? PreviousModelManifest { get; init; }

    /// <summary>
    /// How to recognize. The same kind of system edge as the microphone and the insertion: in
    /// the application it is a model on disk, in a test it is a known answer. Without this seam
    /// the whole dictation path could not be checked at all - the tests would only reach the
    /// start of the recording.
    /// </summary>
    public required TranscribeFactory Transcribe { get; init; }

    /// <summary>How often to poll the permission. Zero - do not poll.</summary>
    public required TimeSpan PermissionPollInterval { get; init; }

    /// <summary>How to download the model. The only network the application has.</summary>
    public required IModelDownloading ModelDownloader { get; init; }

    public required IMicrophoneConsentFlow MicrophoneConsent { get; init; }

    public required ISystemEventSource SystemEvents { get; init; }

    /// <summary>
    /// The only production instance of the engine. <c>null</c> in tests: ASR is substituted
    /// through <see cref="Transcribe"/> and no real model is needed.
    /// </summary>
    public LocalTranscriber? LocalTranscriber { get; init; }

    /// <summary>
    /// How to read the field we inserted into. The only thing the app reads in someone else's
    /// window, so it gets a seam: "switched off means we cannot read" is otherwise unprovable
    /// anywhere but the README.
    /// </summary>
    public IFocusedFieldReading? FocusedFieldReader { get; init; }

    public ILaunchAtLoginService? LaunchAtLogin { get; init; }

    public IHostOnlyClipboard? Clipboard { get; init; }

    public IFolderOpening? Folders { get; init; }

    /// <summary>
    /// Whether the active keyboard layout composes with Right Alt - one of the facts
    /// <see cref="HotkeyAdvice"/> cannot work out on its own.
    /// </summary>
    public Func<bool>? LayoutHasAltGraph { get; init; }

    /// <summary>
    /// Another: the sentence that says where the system setting owning the key is changed.
    /// <c>null</c> leaves the warning without it, which is what a platform with no single
    /// settings page deserves.
    /// </summary>
    /// <remarks>
    /// A string rather than a flag because the platform is the only thing that knows it, and it
    /// is copy - <see cref="HotkeyAdvice.WindowsSettingsHint"/> and
    /// <see cref="HotkeyAdvice.LinuxSettingsHint"/> are the two that ship. Every other word of
    /// the warning is shared, because every other word is true on both.
    /// </remarks>
    public string? HotkeySettingsHint { get; init; }

    /// <summary>
    /// Whether the hotkey source keeps the given key's events from the rest of the system - the
    /// difference between "Right Alt still composes" and "Right Alt is gone". <c>null</c> answers
    /// yes, so a build that forgot it warns rather than stays silent.
    /// </summary>
    public Func<DictationHotkey, bool>? HotkeySourceSwallowsKey { get; init; }

    /// <summary>
    /// Whether keyd's virtual keyboard is present, folding Right Ctrl and Right Shift into their
    /// left codes. <c>null</c> answers no: only Linux has keyd to look for.
    /// </summary>
    public Func<bool>? KeydFoldsRightModifiers { get; init; }

    /// <summary>
    /// The same kind of fact for the microphone: what to tell a person whose microphone will not
    /// open. Required rather than nullable - unlike the hotkey hint, these sentences are the whole
    /// message and not an optional tail, so there is nothing sensible to fall back to.
    /// </summary>
    public required MicrophoneRemedy MicrophoneRemedy { get; init; }

    /// <summary>The clock. Injected so the warm-up countdown stays testable.</summary>
    public Func<DateTimeOffset>? Now { get; init; }

    /// <summary>
    /// Test-only override for the one-hour idle-unload countdown, which suites never sleep
    /// through.
    /// </summary>
    public TimeSpan? IdleUnloadDelayOverride { get; init; }

    /// <summary>
    /// A transient engine failure is not evidence that verified model files are bad. Preparation
    /// retries forever with a backoff ladder; this scale keeps the suite from sleeping
    /// production-scale intervals (<c>null</c> - the real ladder).
    /// </summary>
    public TimeSpan? EngineWarmupRetryDelay { get; init; }

    /// <summary>
    /// How long a finished take may wait for a cold engine before the words are kept for
    /// recovery instead. The kernel's own default is upstream's 25 s, calibrated on a Mac where a
    /// reload is a fraction of a second; a CPU load on a slow PC is not that, so the application
    /// passes 90 s unless told otherwise.
    /// </summary>
    public TimeSpan? PrepareDeadline { get; init; }

    /// <summary>
    /// The monotonic clock the silence rule reads. Injected so a test can hand it two seconds of
    /// quiet without sleeping through them.
    /// </summary>
    public Func<MonotonicInstant>? MonotonicNow { get; init; }
}
