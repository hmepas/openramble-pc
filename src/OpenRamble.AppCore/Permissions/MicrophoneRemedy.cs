namespace OpenRamble.AppCore;

/// <summary>
/// What a person is told to do when the microphone will not open.
/// </summary>
/// <remarks>
/// <para>
/// The peer of <see cref="HotkeyAdvice.WindowsSettingsHint"/>, and here for the same reason. The
/// microphone messages are assembled in AppCore, which is on no platform, yet every one of them
/// ends by naming a remedy - and the remedy is a platform's. Windows has one page that owns the
/// answer, opens on request and has a switch for this application in it; Linux has no such page,
/// no such switch, and nothing that opens - so a sentence promising "the Settings window that
/// just opened" would send a person looking for a window that never appeared.
/// </para>
/// <para>
/// Whole sentences rather than one noun to splice into a shared frame: "Open Windows Settings"
/// and "check that PipeWire is running" are not the same sentence with a different name in it.
/// The set is small and closed - the four notices the application can raise about the microphone,
/// and the three strings of the permission line while it waits.
/// </para>
/// </remarks>
public sealed record MicrophoneRemedy
{
    /// <summary>The capture refused to open: <see cref="AppState.CaptureFailureMessage"/>.</summary>
    public required string AccessRefused { get; init; }

    /// <summary>Launched without the microphone, past onboarding - the tray notice.</summary>
    public required string DictationIsOff { get; init; }

    /// <summary>The microphone went away mid-sentence; the take is being kept.</summary>
    public required string LostWhileDictating { get; init; }

    /// <summary>The microphone went away between dictations.</summary>
    public required string Lost { get; init; }

    /// <summary>
    /// The permission line while <see cref="MicrophoneConsentState.WaitingForSettings"/>: what to
    /// do, what a screen reader reads instead of the tick, and what the button says.
    /// </summary>
    public required string WaitingDetail { get; init; }

    /// <inheritdoc cref="WaitingDetail"/>
    public required string WaitingValue { get; init; }

    /// <inheritdoc cref="WaitingDetail"/>
    public required string WaitingAction { get; init; }

    /// <summary>
    /// Windows: one page owns the microphone, and naming it is the whole remedy.
    /// </summary>
    public static readonly MicrophoneRemedy Windows = new()
    {
        AccessRefused = "No microphone access. Open Windows Settings.",
        DictationIsOff = "Dictation is off: grant Microphone access in Windows Settings.",
        LostWhileDictating = "Microphone access was revoked. Dictation stopped; open Windows Settings.",
        Lost = "Microphone access was revoked. Open Windows Settings.",
        WaitingDetail = "Turn OpenRamble on in the Settings window that just opened, then come back here.",
        WaitingValue = "Waiting for permission in Settings",
        WaitingAction = "Open Settings",
    };

    /// <summary>
    /// Linux: nothing was granted, so nothing can be revoked, and there is nowhere to be sent.
    /// </summary>
    /// <remarks>
    /// A microphone that will not open here means the sound server is not running, the device is
    /// gone or busy, or this user may not have it - see <see cref="IMicrophoneConsentFlow"/>'s
    /// Linux implementation, which learns the answer by opening the device and no other way. So
    /// the remedy is the three things a person can actually check, and the button asks again
    /// rather than offering to open a page that does not exist.
    /// </remarks>
    public static readonly MicrophoneRemedy Linux = new()
    {
        AccessRefused = "Couldn't open the microphone. Check that PipeWire is running and a microphone is connected.",
        DictationIsOff = "Dictation is off: OpenRamble can't open the microphone.",
        LostWhileDictating = "The microphone stopped being available. Dictation stopped; check that it is still connected.",
        Lost = "The microphone is no longer available. Check that it is still connected.",
        WaitingDetail = "The microphone didn't deliver any sound. Check that PipeWire is running and a "
            + "microphone is connected, then try again.",
        WaitingValue = "The microphone could not be opened",
        WaitingAction = "Try again",
    };
}
