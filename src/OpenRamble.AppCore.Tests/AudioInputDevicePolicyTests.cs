using OpenRamble.AppCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Choosing a microphone, and what happens when it is gone. The enumeration scenario needs a
/// real device list and belongs to the platform projects; the rule that reads the list is here.
/// </summary>
public sealed class AudioInputDevicePolicyTests
{
    private static readonly IReadOnlyList<AudioInputDevice> Available =
    [
        new("built-in", "Built-in Microphone"),
        new("usb-headset", "USB Headset"),
    ];

    /// <summary>
    /// The default is the system's, which is what the app did before this setting existed and
    /// what almost everyone wants.
    /// </summary>
    [Fact]
    public void TestScenario001()
    {
        Assert.Null(AudioInputDevicePolicy.Preferred(null, Available));
        Assert.Null(AudioInputDevicePolicy.Preferred("", Available));
        Assert.Null(AudioInputDevicePolicy.Notice(null, Available));
        Assert.Null(AudioInputDevicePolicy.Notice("", Available));
    }

    /// <summary>A device that is plugged in resolves, and nothing is said about it.</summary>
    [Fact]
    public void TestScenario002()
    {
        Assert.Equal(Available[1], AudioInputDevicePolicy.Preferred("usb-headset", Available));
        Assert.Null(AudioInputDevicePolicy.Notice("usb-headset", Available));
    }

    /// <summary>
    /// A microphone that is not on this machine says so rather than being swapped in silence.
    /// Someone who chose a headset and quietly got the laptop lid would only find out from the
    /// transcript.
    /// </summary>
    [Fact]
    public void TestScenario003()
    {
        Assert.Null(AudioInputDevicePolicy.Preferred("a-device-that-is-not-here", Available));
        Assert.NotNull(AudioInputDevicePolicy.Notice("a-device-that-is-not-here", Available));
        Assert.NotNull(AudioInputDevicePolicy.Notice("usb-headset", []));
    }
}
