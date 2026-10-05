using DictationCore.Audio;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.LinuxAudio.Tests;

/// <summary>
/// The microphone list, from <c>pw-dump</c>'s JSON to the picker - and the chosen one, from the
/// picker to the recorder's command line.
/// </summary>
[Collection(LinuxCaptureCollection.Name)]
public sealed class PipeWireInputDevicesTests
{
    /// <summary>A trimmed dump: one microphone, one virtual source, one sink, one stream, one non-node.</summary>
    private const string Dump = """
        [
          { "id": 30, "type": "PipeWire:Interface:Device", "info": { "props": { "device.name": "alsa_card.pci-0000_00_1f.3" } } },
          { "id": 41, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Source",
              "node.name": "alsa_input.pci-0000_00_1f.3.analog-stereo",
              "node.description": "Built-in Audio Analog Stereo",
              "node.nick": "Built-in Audio" } } },
          { "id": 42, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Sink",
              "node.name": "alsa_output.pci-0000_00_1f.3.analog-stereo",
              "node.description": "Built-in Audio Analog Stereo" } } },
          { "id": 43, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Source/Virtual",
              "node.name": "echo-cancel-source",
              "node.nick": "Echo-Cancel" } } },
          { "id": 44, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Stream/Input/Audio",
              "node.name": "pw-record",
              "node.description": "pw-record" } } },
          { "id": 45, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Source",
              "node.description": "A node with no name is not a device anyone can choose" } } }
        ]
        """;

    [Fact]
    public void OnlyCaptureSourcesAreMicrophones()
    {
        IReadOnlyList<AudioInputDevice> devices = PipeWireInputDevices.Parse(Dump);

        Assert.Equal(
            [
                new AudioInputDevice("alsa_input.pci-0000_00_1f.3.analog-stereo", "Built-in Audio Analog Stereo"),
                new AudioInputDevice("echo-cancel-source", "Echo-Cancel"),
            ],
            devices);
    }

    [Fact]
    public void TheNameIsTheNodeNameNotTheNumber()
    {
        // Numbers are handed out per session and reused; a stored 41 could be the webcam tomorrow.
        AudioInputDevice device = PipeWireInputDevices.Parse(Dump)[0];

        Assert.Equal("alsa_input.pci-0000_00_1f.3.analog-stereo", device.Uid);
    }

    [Fact]
    public void AMissingToolIsAnEmptyList()
    {
        var source = new PipeWireInputDevices(() => null);

        Assert.Empty(source.Available());
    }

    [Fact]
    public void ADumpThatIsNotJsonIsAnEmptyListAndALogLine()
    {
        using var diagnostics = new DiagnosticRecorder();
        var source = new PipeWireInputDevices(() => "pw-dump: connection refused");

        Assert.Empty(source.Available());
        Assert.Equal("false", diagnostics.Field("audio.devices", "ok"));
    }

    [Fact]
    public void TheChosenMicrophoneReachesTheRecorderAsATarget()
    {
        string[] arguments = [.. PwRecordProcess.Arguments(new AudioStreamFormat(16_000, 1), "echo-cancel-source")];

        int at = Array.IndexOf(arguments, "--target");
        Assert.True(at >= 0);
        Assert.Equal("echo-cancel-source", arguments[at + 1]);
        // The destination stays last: the pipe, after everything that describes what goes into it.
        Assert.Equal("-", arguments[^1]);
    }

    [Fact]
    public void NoChoiceMeansNoTarget()
    {
        string[] arguments = [.. PwRecordProcess.Arguments(new AudioStreamFormat(16_000, 1), null)];

        Assert.DoesNotContain("--target", arguments);
    }

    [Fact]
    public async Task TheCaptureHandsItsPreferredDeviceToEveryStart()
    {
        string takes = Path.Combine(Path.GetTempPath(), $"openramble-devices-{Guid.NewGuid():N}");
        var targets = new List<string?>();
        using var capture = new PipeWireCapture(takes, target =>
        {
            targets.Add(target);
            var recorder = new FakeRecorderProcess();
            return recorder;
        });

        try
        {
            capture.PreferredDeviceUid = "echo-cancel-source";
            await capture.StartRecording();
            await capture.AbortRecording();

            // Back to the default: the next take asks for nothing in particular.
            capture.PreferredDeviceUid = null;
            await capture.StartRecording();
            await capture.AbortRecording();

            Assert.Equal(["echo-cancel-source", null], targets);
        }
        finally
        {
            if (Directory.Exists(takes)) { Directory.Delete(takes, recursive: true); }
        }
    }
}
