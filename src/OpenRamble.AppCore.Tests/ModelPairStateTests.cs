using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Combining the states of two models into one that a person can see.
///
/// For a person, recognition and term hinting are one "model": one button, one progress bar, one
/// destiny. The union rules are pure policy, and each of them is recorded here.
/// </summary>
public class ModelPairStateTests
{
    private static readonly ModelState Directory = new ModelState.Ready("/tmp/engine");

    private static ModelState Combined(ModelState main, ModelState vocabulary) =>
        ModelPairState.Combine(
            main,
            vocabulary,
            mainTotalBytes: 480,
            vocabularyTotalBytes: 100,
            mainFileCount: 21,
            vocabularyFileCount: 16);

    [Fact]
    public void Scenario001()
    {
        ModelState state = Combined(Directory, new ModelState.Ready("/tmp/ctc"));

        Assert.Equal(Directory, state);
    }

    [Fact]
    public void Scenario002()
    {
        // Top-up after an application update: the main model is already there, the hinter is not.
        // For a person this is "the model is not ready, download the rest".
        Assert.Equal(new ModelState.NotInstalled(), Combined(Directory, new ModelState.NotInstalled()));
    }

    [Fact]
    public void Scenario003()
    {
        ModelState state = Combined(new ModelState.Downloading(50, 480), new ModelState.NotInstalled());

        Assert.Equal(new ModelState.Downloading(50, 580), state);
    }

    [Fact]
    public void Scenario004()
    {
        // The main one is already downloaded: progress has no right to jump back to zero.
        ModelState state = Combined(Directory, new ModelState.Downloading(30, 100));

        Assert.Equal(new ModelState.Downloading(510, 580), state);
    }

    [Fact]
    public void Scenario005()
    {
        ModelState state = Combined(new ModelState.Verifying(3, 21), new ModelState.NotInstalled());

        Assert.Equal(new ModelState.Verifying(3, 37), state);
    }

    [Fact]
    public void Scenario006()
    {
        ModelState state = Combined(Directory, new ModelState.Verifying(3, 16));

        Assert.Equal(new ModelState.Verifying(24, 37), state);
    }

    [Fact]
    public void Scenario007()
    {
        var failure = new ModelStoreError.Download("broken");

        Assert.Equal(
            new ModelState.Failed(failure),
            Combined(new ModelState.Failed(failure), new ModelState.NotInstalled()));
        Assert.Equal(
            new ModelState.Failed(failure),
            Combined(Directory, new ModelState.Failed(failure)));
    }

    [Fact]
    public void Scenario008()
    {
        ModelState state = Combined(Directory, new ModelState.RepairRequired("checksums didn't match"));

        var repair = Assert.IsType<ModelState.RepairRequired>(state);
        Assert.Contains("vocabulary helper", repair.Reason);
        Assert.Contains("checksums didn't match", repair.Reason);
    }

    [Fact]
    public void Scenario009()
    {
        Assert.Equal(new ModelState.Deleting(), Combined(new ModelState.Deleting(), Directory));
        Assert.Equal(new ModelState.Deleting(), Combined(Directory, new ModelState.Deleting()));
    }

    [Fact]
    public void Scenario010()
    {
        // The button must name the real volume: full for a clean installation, only the remainder
        // for a top-up.
        Assert.Equal(
            580,
            ModelPairState.RemainingBytes(
                new ModelState.NotInstalled(), new ModelState.NotInstalled(), 480, 100));
        Assert.Equal(
            100,
            ModelPairState.RemainingBytes(Directory, new ModelState.NotInstalled(), 480, 100));
        Assert.Equal(
            100,
            ModelPairState.RemainingBytes(Directory, new ModelState.RepairRequired("x"), 480, 100));
    }
}
