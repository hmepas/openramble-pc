using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Whether Windows will let this process open a microphone.
/// </summary>
/// <remarks>
/// <para>
/// There is no API that answers this for a desktop application - <c>AppCapability</c> covers
/// packaged apps only - so the answer is read where the Settings page writes it: the capability
/// access consent store. Two switches matter, and they are separate places on purpose:
/// </para>
/// <list type="bullet">
/// <item><description><b>HKLM</b> - "Microphone access" for the whole device. While it is off,
/// the per-app switch is greyed out, so pointing the person at their app row would send them
/// somewhere that cannot help.</description></item>
/// <item><description><b>HKCU</b> - "Let apps access your microphone" on the key itself, and
/// "Let desktop apps access your microphone" on its <c>NonPackaged</c> subkey. Desktop apps are
/// not listed individually under the latter; one switch governs them all. Both are read: the
/// first packaged builds read only the parent, called a denied desktop switch "granted", and
/// went on to open the device - which answered with an access-denied that the person saw as
/// "Couldn't record audio" instead of the Settings hint.</description></item>
/// </list>
/// <para>
/// A missing value means allowed: a machine that never had the page opened has no value written,
/// and treating that as a denial would send every new user to Settings for nothing.
/// </para>
/// </remarks>
public sealed class MicrophonePermissions : IPermissionReading
{
    private const string ConsentPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public bool MicrophoneGranted => DeviceAllowsMicrophone && UserAllowsDesktopApps;

    public static bool DeviceAllowsMicrophone => Reads(Registry.LocalMachine);

    public static bool UserAllowsDesktopApps =>
        Reads(Registry.CurrentUser) && Reads(Registry.CurrentUser, ConsentPath + @"\NonPackaged");

    /// <summary>
    /// The consent state as the interface names it - which needs one distinction the boolean does
    /// not carry: a device-wide switch cannot be fixed from the app's own row in Settings.
    /// </summary>
    public static MicrophoneConsentState State()
    {
        if (!DeviceAllowsMicrophone) { return MicrophoneConsentState.BlockedForDevice; }
        return UserAllowsDesktopApps ? MicrophoneConsentState.Granted : MicrophoneConsentState.Denied;
    }

    private static bool Reads(RegistryKey root, string path = ConsentPath)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            return key?.GetValue("Value") as string is not "Deny";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // A locked-down machine that will not answer is not a denial: the capture attempt is
            // the honest test, and it reports its own failure through AudioCaptureError.
            return true;
        }
    }
}

/// <summary>
/// Asking for the microphone, Windows-style.
/// </summary>
/// <remarks>
/// macOS has a consent prompt the app can raise. Windows has none for desktop applications: the
/// only way to find out is to open the device, and the only remedy is the Settings page. So
/// "request" here means "try, briefly, and tell the truth about what happened" - which is also
/// the check the person's next dictation would perform anyway.
/// </remarks>
public sealed class MicrophoneConsentFlow : IMicrophoneConsentFlow
{
    private readonly Action activate;

    public MicrophoneConsentFlow(Action activate)
    {
        this.activate = activate;
    }

    public Task<bool> Request() => Task.Run(() =>
    {
        if (!MicrophonePermissions.DeviceAllowsMicrophone) { return false; }

        try
        {
            using var devices = new MMDeviceEnumerator();
            using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            // Activating the audio client is where a denial surfaces (E_ACCESSDENIED), and it
            // does so without starting a stream - so asking for permission never lights the
            // system's recording indicator for a person who is not dictating.
            _ = device.AudioClient;
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    });

    /// <summary>
    /// The exact page, not the Privacy root: the person is being sent to flip one switch, and
    /// making them find it is how a permission step turns into an abandoned install.
    /// </summary>
    public void OpenSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose();

    public void ActivateApplication() => activate();
}
