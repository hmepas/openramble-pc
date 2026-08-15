using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Start at login, through the per-user Run key.
/// </summary>
/// <remarks>
/// <c>HKCU\...\Run</c> rather than a scheduled task or a service: it needs no elevation, it is
/// the one place a person looks (Task Manager's Startup tab lists it and can switch it off), and
/// switching it off there must win over whatever this app believes. That is why
/// <see cref="IsEnabled"/> reads the registry every time instead of trusting a saved setting -
/// the registry is the truth, and a settings file that disagreed with it would show the person a
/// switch that lies.
/// </remarks>
public sealed class LaunchAtLogin : ILaunchAtLoginService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenRamble";

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunPath);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true)
            ?? throw new InvalidOperationException("the startup list could not be opened");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("the application's own path is unknown");
        key.SetValue(ValueName, $"\"{executable}\"");
    }
}

/// <summary>
/// A clipboard write that stays on this machine.
/// </summary>
/// <remarks>
/// Delegates to <see cref="TextInserter"/> rather than repeating the Win32: the mandatory
/// restriction formats (<see cref="WindowsClipboardRestrictions"/>) must be identical on every write
/// this application makes, and two copies of that code are two chances for one of them to drift
/// and leak a dictation into Clipboard History.
/// </remarks>
public sealed class HostOnlyClipboard : IHostOnlyClipboard
{
    public void Copy(string text) => TextInserter.CopyHostOnly(text);
}

/// <summary>Show a folder to the person.</summary>
public sealed class FolderOpening : IFolderOpening
{
    public void Open(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
}

/// <summary>
/// Whether the active keyboard layout composes characters with Right Alt.
/// </summary>
/// <remarks>
/// Read from the layout rather than guessed from the locale: the same person switches between a
/// layout that has AltGr and one that does not, several times an hour, and the hotkey warning has
/// to follow. The probe is the standard one - ask the layout which key produces each printable
/// character, and look for one that needs Ctrl+Alt.
/// </remarks>
public static partial class KeyboardLayoutFacts
{
    public static bool HasAltGraph()
    {
        nint layout = GetKeyboardLayout(0);
        for (char character = ' '; character < 0x0500; character++)
        {
            short scan = VkKeyScanExW((ushort)character, layout);
            if (scan == -1) { continue; }

            // High byte: bit 1 = Ctrl, bit 2 = Alt. Both together is AltGr, and no ordinary key
            // assignment uses that pair for a printable character.
            int shiftState = (scan >> 8) & 0xFF;
            if ((shiftState & 0x06) == 0x06) { return true; }
        }

        return false;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetKeyboardLayout")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanExW")]
    private static partial short VkKeyScanExW(ushort character, nint layout);
}
