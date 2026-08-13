using LocalASR;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// What a person will hear in response to a key press that does not start anything.
///
/// Silence in response to a press is indistinguishable from a broken application. Every reason
/// must be named in words, and that is checked by a table.
/// </summary>
public class DictationReadinessTests
{
    private static readonly ModelState Ready = new ModelState.Ready("/tmp/engine");

    private static string? Reason(
        bool microphone = true,
        ModelState? model = null,
        bool engineReady = true) =>
        DictationReadiness.Reason(microphone, model ?? Ready, engineReady);

    [Fact]
    public void Scenario001()
    {
        Assert.Null(Reason());
    }

    /// <summary>
    /// The warm-up window is the very place where there used to be deaf silence: everything
    /// installed, everything granted, but the key does nothing for tens of seconds.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        string? message = Reason(engineReady: false);

        Assert.NotNull(message);
        Assert.Contains("20–40 seconds", message);
    }

    [Fact]
    public void Scenario003()
    {
        Assert.Equal(
            "Dictation needs microphone access. Open Settings → General → Permissions.",
            Reason(microphone: false));
    }

    [Fact]
    public void Scenario005()
    {
        Assert.Contains("model isn't downloaded", Reason(model: new ModelState.NotInstalled()));
    }

    [Fact]
    public void Scenario006()
    {
        Assert.Equal(
            "The recognition model is still downloading.",
            Reason(model: new ModelState.Downloading(10, 100)));
    }

    [Fact]
    public void Scenario007()
    {
        Assert.Contains("needs repair", Reason(model: new ModelState.RepairRequired("checksum")));
        Assert.Contains("needs repair", Reason(model: new ModelState.Failed(new ModelStoreError.Cancelled())));
    }

    /// <summary>
    /// The permission is more important than the model: without it no model helps, and telling a
    /// person to download 670 MB before they granted the microphone is a lie.
    /// </summary>
    [Fact]
    public void Scenario008()
    {
        string? message = Reason(microphone: false, model: new ModelState.NotInstalled());

        Assert.Contains("microphone", message);
        Assert.DoesNotContain("model", message);
    }

    /// <summary>A finished model without a warmed-up engine is not "the model is not installed".</summary>
    [Fact]
    public void Scenario009()
    {
        string? message = Reason(model: Ready, engineReady: false);

        Assert.DoesNotContain("isn't downloaded", message);
    }
}
