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
    /// Whether the active keyboard layout composes with Right Alt - the one fact
    /// <see cref="HotkeyAdvice"/> cannot work out on its own.
    /// </summary>
    public Func<bool>? LayoutHasAltGraph { get; init; }

    /// <summary>The clock. Injected so the residency and warm-up rules stay testable.</summary>
    public Func<DateTimeOffset>? Now { get; init; }
}
