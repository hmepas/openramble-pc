namespace OpenRamble.AppCore;

/// <summary>
/// What every setting is before anyone touches it.
///
/// One place rather than a literal at each reading site: the revert arrow in Settings and the
/// value the app starts with have to agree, and they can only be relied on to agree if they
/// are the same constant. A default that drifts between "what you get" and "what revert gives
/// you" is worse than none - the arrow would quietly move the setting somewhere the person
/// never chose.
/// </summary>
/// <remarks>
/// The macOS list also carries <c>presence</c> (menu bar, Dock, or both) and a default
/// recording shortcut. Neither exists here: a PC tray application has no Dock to move to, and
/// the recorder is not part of this port.
///
/// The paste-last-take chord is not here either: a key code is a platform's number, so its default
/// comes from the composition root (<see cref="AppEnvironment.DefaultLastTakeShortcut"/>) and the
/// revert arrow reads it back from <see cref="AppState.DefaultLastTakeShortcut"/>.
/// </remarks>
public static class SettingsDefaults
{
    /// <summary>Right Ctrl: free on every layout, present on every keyboard.</summary>
    public const DictationHotkey Hotkey = DictationHotkey.RightCtrl;

    public const bool SoundsEnabled = true;
    public const bool AppendsTrailingSpace = false;
    public const bool LaunchAtLogin = false;
    public const bool LearnFromEdits = false;
    public const DictationOverlayPlacement OverlayPlacement = DictationOverlayPlacement.Top;
    public const AppAppearance Appearance = AppAppearance.System;
    public const bool DetailedLogging = true;
    public const bool StopsOnSilence = false;

    /// <summary>No microphone chosen: the system default, as before the setting existed.</summary>
    public const string? InputDeviceUid = null;

    public const int HistoryLimit = DictationHistoryStore.DefaultLimit;
}

/// <summary>
/// Which look the app takes, regardless of the rest of the system.
///
/// <see cref="System"/> is the default and the honest one - an app that follows the machine
/// needs no setting at all. The other two exist because a person who keeps their desktop light
/// and this window dark is not doing anything strange.
/// </summary>
public enum AppAppearance
{
    System,
    Light,
    Dark,
}

public static class AppAppearances
{
    public static IReadOnlyList<AppAppearance> AllCases { get; } =
        [AppAppearance.System, AppAppearance.Light, AppAppearance.Dark];

    /// <summary>Unknown or absent stored values read as the default.</summary>
    public static AppAppearance Parse(string? stored)
    {
        foreach (AppAppearance candidate in AllCases)
        {
            if (string.Equals(candidate.RawValue, stored, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return SettingsDefaults.Appearance;
    }

    extension(AppAppearance appearance)
    {
        public string Title => appearance switch
        {
            AppAppearance.System => "System",
            AppAppearance.Light => "Light",
            AppAppearance.Dark => "Dark",
            _ => throw new ArgumentOutOfRangeException(nameof(appearance)),
        };

        /// <summary>The stored spelling, shared with the macOS defaults value.</summary>
        public string RawValue => appearance switch
        {
            AppAppearance.System => "system",
            AppAppearance.Light => "light",
            AppAppearance.Dark => "dark",
            _ => throw new ArgumentOutOfRangeException(nameof(appearance)),
        };
    }
}
