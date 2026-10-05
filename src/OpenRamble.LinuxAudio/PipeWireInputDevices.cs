using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.LinuxAudio;

/// <summary>
/// The microphones PipeWire can hear through, right now.
///
/// The Linux half of the macOS <c>AudioInputDevices</c>: this asks the server, and
/// <see cref="AudioInputDevicePolicy"/> reads the answer.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>pw-dump</c>, because it ships with <c>pw-record</c>.</b> The capture already depends on
/// PipeWire's command-line tools and names the package they live in; asking the same package for
/// the device list means a machine that can record can also list. <c>pactl</c> would need
/// pipewire-pulse on top, and <c>pw-cli ls Node</c> prints a format meant for eyes. The dump is
/// JSON, and the two properties this reads are the ones every node carries:
/// <c>node.name</c>, which is stable across reboots and is what a stored choice is keyed by, and
/// <c>node.description</c>, which is what the desktop's own sound settings show.
/// </para>
/// <para>
/// <b>Only <c>Audio/Source</c> nodes.</b> A sink's monitor is a source too, in PipeWire's eyes,
/// but it is filed under <c>Audio/Sink</c> and stays out - dictating into what the speakers play
/// is not something anybody asks for by picking a microphone. Application streams
/// (<c>Stream/Input/Audio</c>) are not devices either.
/// </para>
/// <para>
/// <b>Absent is an empty list.</b> No tool, no server, a dump that is not JSON: the picker shows
/// "System default" alone, the way it does before the first device is plugged in, and the
/// reason goes to the diagnostic log rather than to a dialog.
/// </para>
/// </remarks>
public sealed class PipeWireInputDevices : IAudioInputDeviceSource
{
    /// <summary>The dumper, resolved through <c>PATH</c> the way a person would type it.</summary>
    public const string Executable = "pw-dump";

    /// <summary>
    /// How long the dump may take. It answers in tens of milliseconds; a server that takes
    /// longer than this is not one the picker should wait on.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly Func<string?> dump;

    public PipeWireInputDevices()
        : this(RunDump)
    {
    }

    /// <summary>The same source with the tool replaced - the seam the tests drive.</summary>
    internal PipeWireInputDevices(Func<string?> dump)
    {
        this.dump = dump;
    }

    public IReadOnlyList<AudioInputDevice> Available()
    {
        string? json = dump();
        if (json is null) { return []; }

        try
        {
            return Parse(json);
        }
        catch (JsonException error)
        {
            DiagnosticLog.Emit("audio.devices", ("ok", false), ("error", error.Message));
            return [];
        }
    }

    /// <summary>
    /// The capture sources in one <c>pw-dump</c>, in the order the server lists them.
    /// </summary>
    /// <exception cref="JsonException">The dump is not JSON.</exception>
    public static IReadOnlyList<AudioInputDevice> Parse(string json)
    {
        var devices = new List<AudioInputDevice>();
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) { return devices; }

        foreach (JsonElement node in document.RootElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty("info", out JsonElement info)
                || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props)
                || props.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Text(props, "media.class") is not { } mediaClass || !IsMicrophone(mediaClass)) { continue; }

            if (Text(props, "node.name") is not { Length: > 0 } name) { continue; }

            string label = Text(props, "node.description")
                ?? Text(props, "node.nick")
                ?? name;
            devices.Add(new AudioInputDevice(name, label));
        }

        return devices;
    }

    /// <summary><c>Audio/Source</c> and its virtual variant; never a sink's monitor or a stream.</summary>
    private static bool IsMicrophone(string mediaClass) =>
        mediaClass == "Audio/Source" || mediaClass.StartsWith("Audio/Source/", StringComparison.Ordinal);

    private static string? Text(JsonElement props, string key) =>
        props.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Run the dumper and hand back its output, or <c>null</c> when there is none.</summary>
    private static string? RunDump()
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process? process = Process.Start(start);
            if (process is null) { return null; }

            // stderr is drained asynchronously so a chatty tool cannot fill its pipe and stall the
            // read that matters; stdout is read to the end with a deadline on the whole thing.
            process.BeginErrorReadLine();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)Budget.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: false);
                DiagnosticLog.Emit("audio.devices", ("ok", false), ("error", "timeout"));
                return null;
            }

            if (process.ExitCode != 0)
            {
                DiagnosticLog.Emit("audio.devices", ("ok", false), ("exit", process.ExitCode));
                return null;
            }

            return output.GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or IOException)
        {
            // A machine without PipeWire's tools is normal; the picker simply has nothing to add.
            DiagnosticLog.Emit("audio.devices", ("ok", false), ("error", error.Message));
            return null;
        }
    }
}
