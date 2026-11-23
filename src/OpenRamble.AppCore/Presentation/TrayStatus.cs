using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>What the icon is signalling right now.</summary>
public enum TrayActivity
{
    Hidden,
    Recording,
    Working,
}

/// <summary>
/// The dot over the brand mark. One shape, one meaning per color - see
/// <see cref="StatusColorRole"/>: red = the microphone is capturing, blue = working on speech,
/// orange = something waits for the person.
/// </summary>
public enum TrayBadge
{
    Hidden,
    Recording,
    Working,
    Attention,
}

/// <summary>
/// The tray identity and what it says about the current app state.
///
/// The icon is the only permanent presence of the application on the screen. A picture without
/// a description does not exist at all for a blind person, so the brand mark is paired with a
/// state-specific accessibility label below.
/// </summary>
/// <remarks>Ported from <c>MenuBarStatus</c>.</remarks>
public static class TrayStatus
{
    /// <param name="isRecordingMeeting">
    /// A live recording is capturing (not paused): the red dot, whatever dictation is doing short
    /// of listening, as upstream. The shortcut never raises a window, so the dot is its answer.
    /// </param>
    public static TrayActivity Activity(DictationState state, bool isRecordingMeeting = false) => state switch
    {
        // No dot until audio is actually being captured: the red dot must never lie about the
        // microphone, and the overlay already answers the key press instantly.
        _ when isRecordingMeeting => TrayActivity.Recording,
        DictationState.Preparing => TrayActivity.Hidden,
        DictationState.Listening => TrayActivity.Recording,
        DictationState.Transcribing or DictationState.Inserting => TrayActivity.Working,
        DictationState.Idle => TrayActivity.Hidden,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>
    /// Recording and working stay distinguishable without color through the accessibility label
    /// and the menu's first line; the two dots also differ strongly in luminance for
    /// color-blind users.
    /// </summary>
    /// <param name="recordingIsDegraded">
    /// A recording with a side not arriving: orange outranks the steady red, which would say all
    /// is well (upstream <c>MenuBarStatus.badge</c>).
    /// </param>
    public static TrayBadge Badge(TrayActivity activity, bool needsAttention = false, bool recordingIsDegraded = false) => activity switch
    {
        _ when recordingIsDegraded => TrayBadge.Attention,
        TrayActivity.Recording => TrayBadge.Recording,
        TrayActivity.Working => TrayBadge.Working,
        TrayActivity.Hidden => needsAttention ? TrayBadge.Attention : TrayBadge.Hidden,
        _ => throw new ArgumentOutOfRangeException(nameof(activity)),
    };

    /// <summary>
    /// Does setup wait on the person right now?
    ///
    /// True only for states a click can advance: a permission to grant, a download to start, a
    /// repair to confirm. Work in progress - downloading, verifying, deleting, engine warm-up -
    /// is the app's job, and showing "attention" for it would train people to ignore the dot.
    /// </summary>
    /// <remarks>
    /// The macOS original also takes an <c>AccessibilityPermissionState</c>, whose whole
    /// vocabulary (restart required, entry needs repair, repairing) is about TCC keeping a stale
    /// record. Windows has no Accessibility permission and no such record, so the microphone is
    /// the only consent that can be pending - and the "repair is running, nothing to click" case
    /// disappears with it.
    /// </remarks>
    public static bool SetupNeedsUserAction(
        MicrophoneConsentState microphoneState,
        ModelState modelState)
    {
        switch (microphoneState)
        {
            case MicrophoneConsentState.Denied:
            case MicrophoneConsentState.WaitingForSettings:
            case MicrophoneConsentState.BlockedForDevice:
                return true;
            case MicrophoneConsentState.Granted:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(microphoneState));
        }

        return modelState switch
        {
            ModelState.NotInstalled or ModelState.RepairRequired or ModelState.Failed => true,
            ModelState.Downloading or ModelState.Verifying or ModelState.Deleting or ModelState.Ready => false,
            _ => throw new ArgumentOutOfRangeException(nameof(modelState)),
        };
    }

    /// <summary>
    /// Icon description. Starts with the application name: there are many icons in the tray,
    /// and "recording in progress" says nothing without the owner.
    /// </summary>
    public static string AccessibilityLabel(
        DictationState state,
        bool isDictationReady,
        bool hasRecoveredWork = false) => state switch
    {
        DictationState.Listening => "OpenRamble: recording",
        DictationState.Transcribing => "OpenRamble: transcribing speech",
        DictationState.Inserting => "OpenRamble: inserting text",
        DictationState.Preparing => "OpenRamble: turning on the microphone",
        // The dot on the icon must also sound for a screen reader: a picture without words does
        // not exist for the blind.
        DictationState.Idle => hasRecoveredWork
            ? "OpenRamble: last dictation needs attention — open the menu"
            : isDictationReady
                ? "OpenRamble: ready to dictate"
                : "OpenRamble: setup needed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>
    /// The first line of the menu is also an explanation of what to do.
    ///
    /// It talks about unfinished work before it talks about the key. The dictation panel is a
    /// toast: it lives four seconds and leaves, so the lasting explanation has to live
    /// somewhere permanent - the menu. The "Insert Last Dictation" item sits right below this
    /// line.
    /// </summary>
    public static string StatusLine(
        DictationState state,
        bool isDictationReady,
        bool isHandsFreeActive,
        string hotkeyTitle,
        bool hasRecoveredText = false) => state switch
    {
        DictationState.Idle => hasRecoveredText
            ? "Last dictation wasn't inserted"
            // The idle line teaches the one gesture the app has. "Ready" alone told a new
            // person nothing about what to do next.
            : isDictationReady
                ? $"Ready — hold {hotkeyTitle} and speak"
                : "Setup needed",
        DictationState.Preparing => "Turning on the microphone…",
        // In hands-free mode the key is released and recording continues. Not saying so leaves
        // the person with a live microphone and the belief that it is already off.
        DictationState.Listening => isHandsFreeActive
            ? $"Listening — press {hotkeyTitle} to finish"
            : "Listening",
        DictationState.Transcribing => "Transcribing…",
        DictationState.Inserting => "Inserting…",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
