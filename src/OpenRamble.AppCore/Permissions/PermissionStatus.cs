namespace OpenRamble.AppCore;

/// <summary>
/// The state of microphone consent as the running process sees it.
///
/// The macOS original is <c>AccessibilityPermissionState</c>, whose whole vocabulary -
/// restart required, entry needs repair, repairing - describes TCC's habit of keeping a stale
/// record that does not match the current binary. Windows has none of that, and no
/// Accessibility permission at all: a keyboard hook and synthetic input need no consent. What
/// Windows does gate is the microphone, in two independent places, and that is what this type
/// carries.
/// </summary>
public enum MicrophoneConsentState
{
    /// <summary>This app is switched off in Settings → Privacy &amp; security → Microphone.</summary>
    Denied,

    /// <summary>The Settings page was opened for the person; we are waiting for them to come back.</summary>
    WaitingForSettings,

    /// <summary>
    /// Microphone access is off for the whole device, or an administrator policy forbids it.
    /// The per-app switch is greyed out until that one is flipped, so pointing the person at
    /// the app row would send them somewhere that cannot help.
    /// </summary>
    BlockedForDevice,

    Granted,
}

/// <summary>
/// Permission line - the same in onboarding and in settings.
///
/// Both buttons on the screen say "Grant", and they are indistinguishable by ear: without the
/// permission's name, a blind person hears two identical buttons and does not know which is
/// which.
/// </summary>
public sealed record PermissionStatus
{
    private readonly string? explicitValue;

    public PermissionStatus(string title, string detail, bool granted)
    {
        Title = title;
        Detail = detail;
        Granted = granted;
        explicitValue = null;
        ActionTitle = granted ? null : "Grant";
    }

    private PermissionStatus(
        string title,
        string detail,
        bool granted,
        string value,
        string? actionTitle)
    {
        Title = title;
        Detail = detail;
        Granted = granted;
        explicitValue = value;
        ActionTitle = actionTitle;
    }

    public string Title { get; }

    public string Detail { get; }

    public bool Granted { get; }

    /// <summary>The button is only available where there is still something to press.</summary>
    public string? ActionTitle { get; }

    public static PermissionStatus Microphone(MicrophoneConsentState state, string detail) => state switch
    {
        MicrophoneConsentState.Denied => new PermissionStatus(
            "Microphone",
            detail,
            granted: false,
            value: "Permission not granted",
            actionTitle: "Grant"),

        MicrophoneConsentState.WaitingForSettings => new PermissionStatus(
            "Microphone",
            "Turn OpenRamble on in the Settings window that just opened, then come back here.",
            granted: false,
            value: "Waiting for permission in Settings",
            actionTitle: "Open Settings"),

        MicrophoneConsentState.BlockedForDevice => new PermissionStatus(
            "Microphone",
            "Microphone access is turned off for this device, so the switch for OpenRamble "
                + "can't be changed yet. Turn on \"Microphone access\" first.",
            granted: false,
            value: "Microphone access is off for the whole device",
            actionTitle: "Open Settings"),

        MicrophoneConsentState.Granted => new PermissionStatus(
            "Microphone",
            detail,
            granted: true,
            value: "Permission granted",
            actionTitle: null),

        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>The title and the explanation are about the same thing and are read together.</summary>
    public string AccessibilityLabel => $"{Title}. {Detail}";

    /// <summary>A tick with words: the picture itself tells a screen reader nothing.</summary>
    public string AccessibilityValue =>
        explicitValue ?? (Granted ? "Permission granted" : "Permission not granted");

    public string? ActionAccessibilityLabel => ActionTitle is null
        ? null
        : ActionTitle == "Grant" ? $"Grant access: {Title}" : $"{ActionTitle}: {Title}";
}
