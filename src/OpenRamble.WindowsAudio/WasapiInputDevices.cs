using NAudio.CoreAudioApi;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.WindowsAudio;

/// <summary>
/// The microphones this machine can hear through, right now, as WASAPI lists them.
///
/// The Windows half of the macOS <c>AudioInputDevices</c>: the enumeration is here, the rule
/// that reads it - which device is preferred, what to say when it is gone - is
/// <see cref="AudioInputDevicePolicy"/> and is tested without a sound card.
/// </summary>
/// <remarks>
/// <para>
/// Active capture endpoints only. An unplugged or disabled device is not offered, so a person
/// cannot choose a microphone that cannot record; a device chosen earlier and unplugged since is
/// simply absent from the list, and the policy turns that absence into the notice.
/// </para>
/// <para>
/// The UID is the endpoint ID string, which Windows keeps stable for a device across reboots
/// and replugs - the reason it is stored rather than a position in this list. A failure to
/// enumerate (the audio service down) answers with an empty list: there is nothing to choose
/// from, and the capture will say so in its own words when it is asked to start.
/// </para>
/// </remarks>
public sealed class WasapiInputDevices : IAudioInputDeviceSource
{
    public IReadOnlyList<AudioInputDevice> Available()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = new List<AudioInputDevice>();
            foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (endpoint)
                {
                    devices.Add(new AudioInputDevice(endpoint.ID, endpoint.FriendlyName));
                }
            }

            return devices;
        }
        catch (Exception error)
        {
            DiagnosticLog.EmitException("devices.enumerate", error);
            return [];
        }
    }
}
