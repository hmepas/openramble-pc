using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRamble.AppCore;

/// <summary>
/// Everything the person chose, and nothing else.
///
/// Six values. There is no telemetry identifier, no install date, no counters: a settings file
/// that grows things the person never set is a settings file that has started reporting.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The hotkey's stable name. Unknown or missing - the default key.</summary>
    public string? Hotkey { get; init; }

    public bool SoundsEnabled { get; init; } = true;

    /// <summary>BCP-47 code, or <c>null</c> for automatic detection by sound.</summary>
    public string? RecognitionLanguage { get; init; }

    /// <summary>
    /// Reading the field the app inserted into, to learn from edits. Off unless the person
    /// turned it on - this is the only place the app looks inside someone else's window.
    /// </summary>
    public bool LearnFromEdits { get; init; }

    public bool OnboardingCompleted { get; init; }
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

    public void Write(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions));
    }
}
