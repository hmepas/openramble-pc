using DictationCore;
using DictationCore.Audio;

namespace OpenRamble.LinuxAudio.Tests;

/// <summary>
/// The command line and the one failure that happens before any audio does.
/// </summary>
/// <remarks>
/// The command line is the whole specification of the format the capture receives: get one flag
/// wrong and the samples are 48 kHz stereo Int16 read as 16 kHz mono Float32, which produces a
/// recording that plays as noise and no error anywhere. It is asserted literally, argument for
/// argument, because that is the only way a change to it can be a deliberate one.
/// </remarks>
[Collection(LinuxCaptureCollection.Name)]
public sealed class PwRecordProcessTests
{
    [Fact]
    public void TheCommandLineAsksForExactlyTheRecordingFormat()
    {
        string[] arguments = [.. PwRecordProcess.Arguments(new AudioStreamFormat(16_000, 1))];

        Assert.Equal(
            [
                "--rate", "16000",
                "--channels", "1",
                "--format", "f32",
                "--raw",
                "--latency", "20ms",
                "--media-role", "Communication",
                "-P", "media.name=\"OpenRamble dictation\"",
                "-",
            ],
            arguments);
    }

    [Fact]
    public void TheFormatComesFromTheCapture()
    {
        // Not a constant of its own: a rate written twice is a rate that can disagree with itself,
        // and the disagreement would be inaudible until recognition returned nothing.
        string[] arguments = [.. PwRecordProcess.Arguments(new AudioStreamFormat(48_000, 2))];

        Assert.Equal("48000", arguments[Array.IndexOf(arguments, "--rate") + 1]);
        Assert.Equal("2", arguments[Array.IndexOf(arguments, "--channels") + 1]);
    }

    [Fact]
    public void RawSamplesGoToStandardOutput()
    {
        string[] arguments = [.. PwRecordProcess.Arguments(new AudioStreamFormat(16_000, 1))];

        // The last argument is the destination, and "-" is the pipe. A file name there would
        // record the dictation to disk twice and hand the capture nothing.
        Assert.Equal("-", arguments[^1]);
        Assert.Contains("--raw", arguments);
    }

    [LinuxFact]
    public void AMissingRecorderNamesTheToolAndWhereToGetIt()
    {
        // A machine without PipeWire's command-line tools is an ordinary machine, and "engine
        // unavailable" teaches the person nothing they can act on.
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "/nonexistent/openramble-has-no-recorder");

            AudioCaptureError.EngineUnavailable error =
                Assert.Throws<AudioCaptureError.EngineUnavailable>(
                    () => PwRecordProcess.Launch(new AudioStreamFormat(16_000, 1)));

            Assert.Contains("pw-record", error.Message, StringComparison.Ordinal);
            Assert.Contains("pipewire", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }
}

/// <summary>
/// A fact that starts a real process, and is skipped where the process would mean something else.
/// </summary>
/// <remarks>
/// The capture's logic is platform-neutral and its tests run anywhere; this one is about how
/// Linux reports a missing executable, and asserting it on another operating system would assert
/// that operating system's error message.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "The recorder is a Linux tool; this checks how Linux reports its absence.";
        }
    }
}
