namespace OpenRamble.AppCore.Audio;

/// <summary>One microphone the machine can hear through.</summary>
/// <param name="Uid">
/// The stable name for a device across reboots and replugs.
///
/// Not a numeric index: those are handed out per session and get reused, so a stored one can
/// point at a different device tomorrow - or at the wrong one after unplugging a dock. Storing
/// the UID means a chosen microphone is still the same microphone next week. On Windows it is
/// the WASAPI endpoint ID; on PipeWire the node's <c>node.name</c>.
/// </param>
public sealed record AudioInputDevice(string Uid, string Name);

/// <summary>
/// Which microphone dictation listens through, given what is plugged in right now.
///
/// The pure half of the macOS <c>AudioInputDevices</c>: enumeration is a platform call and
/// lives with the capture, this is the rule that reads its answer.
/// </summary>
public static class AudioInputDevicePolicy
{
    /// <summary>
    /// The chosen microphone as the platform knows it, or <c>null</c> for the default.
    ///
    /// A device that has been unplugged resolves to <c>null</c> rather than failing: dictation
    /// still works through the default input, and <see cref="Notice"/> is what makes sure the
    /// person is told rather than left wondering why their headset is not being used.
    /// </summary>
    public static AudioInputDevice? Preferred(string? uid, IReadOnlyList<AudioInputDevice> available)
    {
        if (string.IsNullOrEmpty(uid)) { return null; }
        foreach (AudioInputDevice device in available)
        {
            if (string.Equals(device.Uid, uid, StringComparison.Ordinal)) { return device; }
        }

        return null;
    }

    /// <summary>
    /// Set when the chosen microphone is not on this machine.
    ///
    /// Said out loud rather than swapped silently: someone who chose a headset and quietly got
    /// the laptop lid would only find out from the transcript.
    /// </summary>
    public static string? Notice(string? uid, IReadOnlyList<AudioInputDevice> available)
    {
        if (string.IsNullOrEmpty(uid)) { return null; }
        if (Preferred(uid, available) is not null) { return null; }
        return "The microphone you chose isn't connected. Dictation is using the system default.";
    }
}
