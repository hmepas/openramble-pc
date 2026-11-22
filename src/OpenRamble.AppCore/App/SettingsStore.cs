using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRamble.AppCore;

/// <summary>
/// Everything the person chose, and nothing else.
///
/// Fifteen values, every one of them a choice made in Settings. There is no telemetry
/// identifier, no install date, no counters: a settings file that grows things the person never
/// set is a settings file that has started reporting.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The hotkey's stable name. Unknown or missing - the default key.</summary>
    public string? Hotkey { get; init; }

    public bool SoundsEnabled { get; init; } = true;

    /// <summary>
    /// Also leave each finished dictation on the clipboard. Off by default: dictated text on the
    /// clipboard is dictated text handed to every clipboard manager on the machine.
    /// </summary>
    public bool CopiesToClipboard { get; init; }

    /// <summary>
    /// The chord that copies the last dictation, stored as <see cref="KeyCombination.RawValue"/>;
    /// <c>null</c> or unparsable - no shortcut.
    /// </summary>
    public string? CopyShortcut { get; init; }

    /// <summary>End every dictation with a space. For dictating in runs.</summary>
    public bool AppendsTrailingSpace { get; init; }

    /// <summary>Light, dark, or whatever the machine is doing. Stored spelling: <see cref="AppAppearances"/>.</summary>
    public string? Appearance { get; init; }

    /// <summary>Which microphone to record through, by its stable UID; <c>null</c> - the system default.</summary>
    public string? InputDeviceUid { get; init; }

    /// <summary>
    /// Keep the local diagnostic journal. On when the file never said otherwise; a stored
    /// <c>false</c> - the opt-out, or the old opt-in log never turned on - is kept.
    /// </summary>
    public bool DetailedLogging { get; init; } = SettingsDefaults.DetailedLogging;

    /// <summary>Finish a hands-free dictation on two seconds of quiet.</summary>
    public bool StopsOnSilence { get; init; }

    /// <summary>Where the dictation panel sits. Stored spelling: <see cref="DictationOverlayPlacements"/>.</summary>
    public string? OverlayPlacement { get; init; }

    /// <summary>How many dictations the history keeps; <c>null</c> - the default.</summary>
    public int? HistoryLimit { get; init; }

    /// <summary>
    /// Reading the field the app inserted into, to learn from edits. Off unless the person
    /// turned it on - this is the only place the app looks inside someone else's window.
    /// </summary>
    public bool LearnFromEdits { get; init; }

    /// <summary>
    /// The revision of the setup flow the person has been through; <c>0</c> - never. Compared
    /// against <see cref="OnboardingRevision.Current"/>, so an update shows the flow again only
    /// when we decided it should - never merely because the version changed.
    /// </summary>
    public int OnboardingCompletedRevision { get; init; }
}

/// <summary>
/// The settings file.
///
/// macOS keeps these in <c>UserDefaults</c>. Windows has no such store, so they go to one small
/// JSON file next to the dictionary, under <see cref="AppPaths"/>.
///
/// <b>An unreadable file is not an empty file.</b> Same rule as
/// <see cref="ReplacementsStore"/>: a file that cannot be parsed is left exactly where it is and
/// the defaults are used in memory, so a bad write or a half-flushed disk cannot quietly erase
/// the person's hotkey. It is overwritten only when they change something.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string path;

    public SettingsStore(string path)
    {
        this.path = path;
    }

    public static SettingsStore Under(AppPaths paths) =>
        new(Path.Combine(paths.Support(), "settings.json"));

    public AppSettings Read()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path), SerializerOptions)
                ?? new AppSettings();
        }
        catch (FileNotFoundException)
        {
            return new AppSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Save the settings - all of them, or none.
    /// </summary>
    /// <remarks>
    /// Written beside the file, then moved over it. A plain write is not one step: a process
    /// killed between the truncation and the flush leaves a half-written file, a half-written file
    /// does not parse, and <see cref="Read"/> then falls back to the defaults - so a crash quietly
    /// takes the person's chosen dictation key away and puts the first-launch flow back on screen.
    /// What this file holds is a handful of values somebody set by hand; losing them to a crash
    /// is not acceptable when the cure is a rename.
    /// </remarks>
    public void Write(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string staging = path + ".new";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions));
        File.Move(staging, path, overwrite: true);
    }
}
