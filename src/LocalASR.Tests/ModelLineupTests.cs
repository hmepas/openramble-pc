namespace LocalASR.Tests;

/// <summary>
/// The rule for moving from one recognition model to the next.
///
/// A person who updates the app keeps dictating with the model already on their machine. The
/// better model is offered, downloaded only when they ask, and used from the moment it verifies;
/// only then does the old one go.
/// </summary>
/// <remarks>Ported from upstream's <c>ModelLineupTests.swift</c>.</remarks>
public class ModelLineupTests
{
    private static readonly ModelState Ready = new ModelState.Ready("/models/x");

    [Fact]
    public void ANewInstallationUsesTheCurrentModel()
    {
        var lineup = new ModelLineup(new ModelState.NotInstalled(), new ModelState.NotInstalled());
        Assert.Equal(ModelLineup.Slot.None, lineup.Recognizer);
        Assert.False(lineup.OffersUpgrade);
        Assert.False(lineup.RemovesPrevious);
        Assert.Equal(ModelLineup.Slot.Current, lineup.Installs);
    }

    [Fact]
    public void AnUpdatedMachineKeepsDictatingWithItsModelAndIsOfferedTheNewOne()
    {
        var lineup = new ModelLineup(new ModelState.NotInstalled(), Ready);
        Assert.Equal(ModelLineup.Slot.Previous, lineup.Recognizer);
        Assert.True(lineup.OffersUpgrade);
        Assert.False(lineup.RemovesPrevious);
    }

    [Fact]
    public void TheOldModelServesWhileTheNewOneDownloadsAndVerifies()
    {
        foreach (ModelState state in new ModelState[] { new ModelState.Downloading(1, 2), new ModelState.Verifying(0, 1) })
        {
            var lineup = new ModelLineup(state, Ready);
            Assert.Equal(ModelLineup.Slot.Previous, lineup.Recognizer);
            Assert.False(lineup.OffersUpgrade);
            Assert.False(lineup.RemovesPrevious);
        }
    }

    [Fact]
    public void AFailedOrDamagedUpgradeFallsBackToTheOldModelAndCanBeRetried()
    {
        foreach (ModelState state in new ModelState[]
        {
            new ModelState.Failed(new ModelStoreError.Download("offline")),
            new ModelState.RepairRequired("checksum"),
        })
        {
            var lineup = new ModelLineup(state, Ready);
            Assert.Equal(ModelLineup.Slot.Previous, lineup.Recognizer);
            Assert.True(lineup.OffersUpgrade);
            Assert.False(lineup.RemovesPrevious);
        }
    }

    [Fact]
    public void TheOldModelGoesOnlyOnceTheNewOneIsReady()
    {
        var lineup = new ModelLineup(Ready, Ready);
        Assert.Equal(ModelLineup.Slot.Current, lineup.Recognizer);
        Assert.False(lineup.OffersUpgrade);
        Assert.True(lineup.RemovesPrevious);
    }

    [Fact]
    public void AnOldModelThatIsDamagedIsNotUsedButTheNewOneIsStillOffered()
    {
        var lineup = new ModelLineup(new ModelState.NotInstalled(), new ModelState.RepairRequired("missing file"));
        Assert.Equal(ModelLineup.Slot.None, lineup.Recognizer);
        Assert.Equal(ModelLineup.Slot.Current, lineup.Installs);
        Assert.True(lineup.RemovesPrevious, "a damaged old copy is never repaired, only replaced");
    }

    [Fact]
    public void NothingIsRemovedWhileTheNewModelIsBeingDeleted()
    {
        var lineup = new ModelLineup(new ModelState.Deleting(), Ready);
        Assert.Equal(ModelLineup.Slot.Previous, lineup.Recognizer);
        Assert.False(lineup.RemovesPrevious);
    }

    /// <summary>
    /// Upstream pins Ultra over v3 GGUF. Here the previous model is this port's own v3 ONNX
    /// build, so the two also differ in engine - and must still install into different folders.
    /// </summary>
    [Fact]
    public void TheShippedLineupReplacesV3WithParakeetUltra()
    {
        ModelManifest current = ModelManifest.BundledUltra();
        ModelManifest previous = ModelManifest.Bundled();
        Assert.Equal("parakeet-ultra-gguf", current.ModelID);
        Assert.Equal("parakeet-tdt-0.6b-v3", previous.ModelID);
        Assert.NotEqual(current.ModelID, previous.ModelID);
    }
}
