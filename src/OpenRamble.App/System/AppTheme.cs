using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Light, dark, or whatever Windows is doing - applied to every window at once.
/// </summary>
/// <remarks>
/// <para>
/// The windows read four brushes through <c>DynamicResource</c> (see <c>App.xaml</c>); this swaps
/// them in the application's resources, which is what makes an open window change without being
/// rebuilt. <see cref="AppAppearance.System"/> reads the value Windows keeps for its own apps and
/// follows it live: a person who lets the machine go dark at sunset gets the settings window dark
/// with it, through the same <c>UserPreferenceChanged</c> the shell sends every app.
/// </para>
/// <para>
/// Deliberately small. Only the ground and the text are themed; buttons, lists and pickers stay
/// the shell's own controls, which are readable on either ground because they bring their own.
/// A full dark restyle of WPF's control templates is a theme package, and this port carries none.
/// </para>
/// </remarks>
public static class AppTheme
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppAppearance current = SettingsDefaults.Appearance;
    private static bool listening;

    /// <summary>Apply the choice now, and keep following the system while it says System.</summary>
    public static void Apply(AppAppearance appearance)
    {
        current = appearance;
        if (!listening)
        {
            listening = true;
            SystemEvents.UserPreferenceChanged += (_, args) =>
            {
                // The theme arrives as a General change; refresh on any of them, it costs nothing.
                if (current == AppAppearance.System)
                {
                    Application.Current?.Dispatcher.BeginInvoke(Refresh);
                }
            };
        }

        Refresh();
    }

    private static void Refresh()
    {
        bool dark = current switch
        {
            AppAppearance.Dark => true,
            AppAppearance.Light => false,
            _ => SystemPrefersDark(),
        };

        ResourceDictionary resources = Application.Current.Resources;
        resources["WindowBackgroundBrush"] = Frozen(dark ? 0x1E1E20u : 0xF5F5F7u);
        resources["WindowForegroundBrush"] = Frozen(dark ? 0xF5F5F7u : 0x1C1C1Eu);
        resources["SecondaryForegroundBrush"] = Frozen(dark ? 0xA1A1A6u : 0x6E6E73u);
        resources["PanelBorderBrush"] = Frozen(dark ? 0x3A3A3Eu : 0xD2D2D7u);
    }

    /// <summary>
    /// What Windows keeps for its own apps. Missing key or value - a machine without the
    /// personalization page - reads as light, which is what Windows itself does.
    /// </summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static SolidColorBrush Frozen(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
