using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// When does setup genuinely wait on the person.
///
/// True only for states a click can advance. Work in progress — downloads, verification,
/// deletion — must not wear the attention dot, or people learn to ignore it.
/// </summary>
public class SetupAttentionTests
{
    private static bool NeedsAction(
        MicrophoneConsentState microphone = MicrophoneConsentState.Granted,
        ModelState? model = null) =>
        TrayStatus.SetupNeedsUserAction(microphone, model ?? new ModelState.Ready("/tmp/model"));

    [Fact]
    public void Scenario040()
    {
        Assert.False(NeedsAction());
    }

    [Fact]
    public void Scenario041()
    {
        Assert.True(NeedsAction(MicrophoneConsentState.Denied));
        Assert.True(NeedsAction(MicrophoneConsentState.WaitingForSettings));
        Assert.True(NeedsAction(MicrophoneConsentState.BlockedForDevice));
    }

    [Fact]
    public void Scenario042()
    {
        Assert.True(NeedsAction(model: new ModelState.NotInstalled()));
        Assert.True(NeedsAction(model: new ModelState.RepairRequired("bad file")));
        Assert.True(NeedsAction(model: new ModelState.Failed(new ModelStoreError.Download("offline"))));
    }

    [Fact]
    public void Scenario043()
    {
        Assert.False(NeedsAction(model: new ModelState.Downloading(1, 2)));
        Assert.False(NeedsAction(model: new ModelState.Verifying(1, 2)));
        Assert.False(NeedsAction(model: new ModelState.Deleting()));
    }
}
