using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenRamble.AppCore;

/// <summary>
/// Everything the person chose, and nothing else.
///
/// Twelve values, every one of them a choice made in Settings. There is no telemetry
/// identifier, no install date, no counters: a settings file that grows things the person never
/// set is a settings file that has started reporting. The one value nobody chose is
/// <see cref="SchemaVersion"/>, and it describes the file, not the person.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The hotkey's stable name. Unknown or missing - the default key.</summary>
    public string? Hotkey { get; init; }

    public bool SoundsEnabled { get; init; } = true;

    /// <summary>
    /// The chord that pastes the last take, stored as <see cref="KeyCombination.RawValue"/>;
    /// <c>null</c> - never chosen, so the platform's default; empty or unparsable - cleared.
    /// </summary>
    /// <remarks>
    /// The stored name is the one it had when the chord copied the last dictation, as upstream
    /// kept its <c>copyShortcut</c> key: a chord someone chose then is theirs now, pasting.
    /// </remarks>
    public string? CopyShortcut { get; init; }

    /// <summary>
    /// The chord that starts and stops a recording, stored as <see cref="KeyCombination.RawValue"/>;
    /// <c>null</c> - never chosen, so the platform's default; empty or unparsable - cleared.
    /// </summary>
    public string? RecordingShortcut { get; init; }

    /// <summary>End every dictation with a space. For dictating in runs.</summary>
    public bool AppendsTrailingSpace { get; init; }

    /// <summary>Light, dark, or whatever the machine is doing. Stored spelling: <see cref="AppAppearances"/>.</summary>
    public string? Appearance { get; init; }

    /// <summary>Which microphone to record through, by its stable UID; <c>null</c> - the system default.</summary>
    public string? InputDeviceUid { get; init; }

    /// <summary>
    /// Keep the local diagnostic journal. On when the file never said otherwise; a stored
    /// <c>false</c> is the opt-out and is kept. The <c>false</c> files from before the journal
    /// carry - the old opt-in log, off by default - is turned on once by the 0 -> 1 migration.
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

    /// <summary>
    /// Which shape of this file the values are in. Not a choice and not a counter: it moves only
    /// when a release changes what a stored value means, and <see cref="SettingsStore"/> uses it
    /// to bring an older file up to date exactly once. Settings made in memory are always in the
    /// current shape; a file without the field is from before it existed and reads as <c>0</c>.
    /// </summary>
    public int SchemaVersion { get; init; } = SettingsStore.CurrentSchemaVersion;
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

    /// <summary>Keys are matched the way <see cref="SerializerOptions"/> matches them.</summary>
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The shape <see cref="Write"/> produces; see <see cref="Migrate"/> for the steps up to it.</summary>
    public const int CurrentSchemaVersion = 2;

    private readonly string path;

    public SettingsStore(string path)
    {
        this.path = path;
    }

    public static SettingsStore Under(AppPaths paths) =>
        new(Path.Combine(paths.Support(), "settings.json"));

    /// <summary>
    /// The settings as the current version understands them. An older file is migrated (see
    /// <see cref="Migrate"/>) and written back at once, so its steps run on one launch only.
    /// </summary>
    public AppSettings Read()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path), NodeOptions) is not JsonObject document)
            {
                return new AppSettings();
            }

            bool migrated = Migrate(document);
            AppSettings settings = document.Deserialize<AppSettings>(SerializerOptions) ?? new AppSettings();
            if (migrated)
            {
                try
                {
                    Write(settings);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Not saved this time; the file is still the old shape, and the same steps
                    // give the same answer on the next launch.
                }
            }

            return settings;
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
    /// Bring a stored file up to <see cref="CurrentSchemaVersion"/>, one step per version, in
    /// order. Each step runs once in the life of a file: the version it leaves behind is written
    /// with everything else, and a file already at that version skips it. Returns whether anything
    /// ran.
    /// </summary>
    /// <remarks>
    /// The steps work on the raw document rather than on <see cref="AppSettings"/> because what
    /// they deal with is exactly what the record cannot express: keys that no longer exist, and
    /// values whose meaning changed under the same name. A new step goes at the bottom with the
    /// next number, and <see cref="CurrentSchemaVersion"/> moves with it.
    /// </remarks>
    private static bool Migrate(JsonObject document)
    {
        int version = document["schemaVersion"] is JsonValue stored && stored.TryGetValue(out int number)
            ? number
            : 0;
        if (version >= CurrentSchemaVersion)
        {
            return false;
        }

        if (version < 1)
        {
            // 0 -> 1: the diagnostic journal. Upstream keeps it on unless the key says otherwise,
            // but this file stored every value, and the opt-in log it replaced was off by default -
            // so the "false" nearly every older file carries is a default nobody chose, not an
            // opt-out. Upstream would read it as absent; here it is turned on, once. Somebody who
            // had also switched the old log off and on again gets it on too: the file cannot tell
            // them apart, and the switch is one click away.
            document["detailedLogging"] = true;
            // The "Unload model" picker is gone; the engine keeps one fixed hour for everyone.
            document.Remove("modelUnloadTimeout");
        }

        if (version < 2)
        {
            // 1 -> 2: "Also copy dictations to the clipboard" is gone (upstream 0.33).
            document.Remove("copiesToClipboard");
        }

        document["schemaVersion"] = CurrentSchemaVersion;
        return true;
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
