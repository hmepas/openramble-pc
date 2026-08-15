using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The permission line is the only thing a person does in onboarding with their hands.
/// </summary>
public sealed class PermissionStatusTests
{
    private static PermissionStatus Microphone(bool granted) =>
        new("Microphone", "To hear your speech.", granted);

    [Fact]
    public void Scenario001()
    {
        PermissionStatus status = Microphone(granted: false);

        Assert.Equal("Grant", status.ActionTitle);
        Assert.Equal("Permission not granted", status.AccessibilityValue);
    }

    /// <summary>A granted permission shows no button: there is nothing left to press.</summary>
    [Fact]
    public void Scenario002()
    {
        PermissionStatus status = Microphone(granted: true);

        Assert.Null(status.ActionTitle);
        Assert.Null(status.ActionAccessibilityLabel);
        Assert.Equal("Permission granted", status.AccessibilityValue);
    }

    /// <summary>The tick itself says nothing to a screen reader.</summary>
    [Fact]
    public void Scenario003()
    {
        Assert.NotEqual(Microphone(granted: true).AccessibilityValue, Microphone(granted: false).AccessibilityValue);
    }

    /// <summary>
    /// Both buttons on the screen say "Grant".
    ///
    /// Without the permission's name, a blind person hears two identical buttons and does not
    /// know which is which. Windows only asks for one permission today, but the line is shared
    /// with whatever comes next, and the label has to keep naming its own row.
    /// </summary>
    [Fact]
    public void Scenario004()
    {
        PermissionStatus microphone = Microphone(granted: false);
        var notifications = new PermissionStatus("Notifications", "To tell you when it fails.", granted: false);

        Assert.Equal(microphone.ActionTitle, notifications.ActionTitle);
        Assert.Equal("Grant access: Microphone", microphone.ActionAccessibilityLabel);
        Assert.Equal("Grant access: Notifications", notifications.ActionAccessibilityLabel);
        Assert.NotEqual(microphone.ActionAccessibilityLabel, notifications.ActionAccessibilityLabel);
    }

    [Fact]
    public void Scenario005()
    {
        Assert.Equal("Microphone. To hear your speech.", Microphone(granted: false).AccessibilityLabel);
    }

    /// <summary>
    /// Waiting on the Settings window is not the same as being refused: the button must offer to
    /// reopen it, not to ask again.
    /// </summary>
    [Fact]
    public void Scenario006()
    {
        PermissionStatus status = PermissionStatus.Microphone(
            MicrophoneConsentState.WaitingForSettings,
            "To hear your speech.",
            MicrophoneRemedy.Windows);

        Assert.Equal("Open Settings", status.ActionTitle);
        Assert.Equal("Waiting for permission in Settings", status.AccessibilityValue);
    }

    /// <summary>
    /// The same state on Linux, where no window opened and none will.
    ///
    /// It is reached the same way - a refused request - but the request there is an attempt to
    /// open the device itself, so the remedy is the device and the button asks again instead of
    /// offering a page that does not exist.
    /// </summary>
    [Fact]
    public void TheWaitingLineIsThePlatformsOwn()
    {
        PermissionStatus status = PermissionStatus.Microphone(
            MicrophoneConsentState.WaitingForSettings,
            "To hear your speech.",
            MicrophoneRemedy.Linux);

        Assert.Equal("Try again", status.ActionTitle);
        Assert.Equal("Try again: Microphone", status.ActionAccessibilityLabel);
        Assert.DoesNotContain("Settings", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings", status.AccessibilityValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not one word of the Linux copy may name Windows or a Settings page.
    ///
    /// The whole point of the seam is that these sentences are the platform's; a Linux build that
    /// picked up a Windows sentence would send a person to a window that cannot exist, which is
    /// exactly the bug this replaces.
    /// </summary>
    [Fact]
    public void TheLinuxRemedyNamesNothingLinuxDoesNotHave()
    {
        string[] sentences =
        [
            MicrophoneRemedy.Linux.AccessRefused,
            MicrophoneRemedy.Linux.DictationIsOff,
            MicrophoneRemedy.Linux.LostWhileDictating,
            MicrophoneRemedy.Linux.Lost,
            MicrophoneRemedy.Linux.WaitingDetail,
            MicrophoneRemedy.Linux.WaitingValue,
            MicrophoneRemedy.Linux.WaitingAction,
        ];

        foreach (string sentence in sentences)
        {
            Assert.False(string.IsNullOrWhiteSpace(sentence));
            Assert.DoesNotContain("Windows", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("Settings", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("revoked", sentence, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The device-wide switch is off, so the per-app row is greyed out. Sending the person to
    /// the app row would send them where nothing can be changed.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        PermissionStatus status = PermissionStatus.Microphone(
            MicrophoneConsentState.BlockedForDevice,
            "To hear your speech.",
            MicrophoneRemedy.Windows);

        Assert.Equal("Open Settings", status.ActionTitle);
        Assert.Equal("Microphone access is off for the whole device", status.AccessibilityValue);
        Assert.Contains("Microphone access", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario008()
    {
        PermissionStatus granted = PermissionStatus.Microphone(
            MicrophoneConsentState.Granted,
            "To hear your speech.",
            MicrophoneRemedy.Windows);

        Assert.True(granted.Granted);
        Assert.Null(granted.ActionTitle);
        Assert.Equal("Permission granted", granted.AccessibilityValue);
    }

    /// <summary>Every state that is not granted must leave something to read and something to say.</summary>
    [Fact]
    public void Scenario009()
    {
        MicrophoneConsentState[] states =
        [
            MicrophoneConsentState.Denied,
            MicrophoneConsentState.WaitingForSettings,
            MicrophoneConsentState.BlockedForDevice,
            MicrophoneConsentState.Granted,
        ];

        foreach (MicrophoneConsentState state in states)
        {
            PermissionStatus status = PermissionStatus.Microphone(
                state,
                "To hear your speech.",
                MicrophoneRemedy.Windows);
            Assert.False(string.IsNullOrWhiteSpace(status.AccessibilityValue));
            Assert.False(string.IsNullOrWhiteSpace(status.AccessibilityLabel));
            Assert.Equal(state == MicrophoneConsentState.Granted, status.ActionTitle is null);
        }
    }
}
