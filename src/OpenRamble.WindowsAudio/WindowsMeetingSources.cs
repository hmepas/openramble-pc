using DictationCore.Audio;

namespace OpenRamble.WindowsAudio;

/// <summary>
/// Meeting sources on Windows. System audio (WASAPI loopback) is not ported yet, so there is
/// none: a meeting records the microphone only and reports the other side as unavailable.
/// </summary>
public static class WindowsMeetingSources
{
    /// <summary>Always <c>null</c> until a WASAPI loopback source exists.</summary>
    public static IMeetingAudioSource? SystemAudio() => null;
}
