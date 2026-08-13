namespace OpenRamble.AppCore;

/// <summary>
/// How often to request permissions.
///
/// Permissions are granted and revoked in the system settings, and the system does not tell
/// the application about it - the status has to be asked for.
///
/// There was a temptation to slow the polling down once everything was granted: the app lives
/// in the tray for weeks, and a one-second timer all that time is millions of wake-ups for an
/// answer that changes once. This was deliberately abandoned. A revoke does not send a
/// reliable event, and an app whose microphone access was taken away looks intact and silently
/// does not work - the tray icon must say so no later than two seconds, including from
/// complete rest. While the safe beta is ongoing, the integrity of the icon is worth more than
/// the saved wake-ups.
///
/// Hence the shape of the function: granted permissions do not affect the interval, and the
/// only way to turn polling off is to pass a zero base from outside. The slow-down can come
/// back when a reliable revoke notification appears - but not before.
/// </summary>
/// <remarks>
/// The macOS original takes two flags, <c>accessibilityGranted</c> and
/// <c>microphoneGranted</c>. Windows has no Accessibility permission at all - injecting input
/// and installing a keyboard hook need no consent - so only the microphone flag survives. The
/// policy itself is unchanged, and so is the reason it exists: Windows revokes microphone
/// access in Settings → Privacy &amp; security → Microphone, just as quietly.
/// </remarks>
public static class PermissionPollPolicy
{
    /// <summary>Returns the interval between polls. Zero - do not poll.</summary>
    public static TimeSpan Interval(bool microphoneGranted, TimeSpan @base)
    {
        // Zero means "polling is turned off externally" - that is what tests do.
        if (@base <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // The flag is deliberately not used: see the decision in the type header. It remains in
        // the signature because it is what will come back into the calculation once a revoke
        // has a reliable notification.
        _ = microphoneGranted;
        return @base < Ceiling ? @base : Ceiling;
    }

    /// <summary>The promise: an icon that lies for no longer than this.</summary>
    public static TimeSpan Ceiling { get; } = TimeSpan.FromSeconds(2);
}
