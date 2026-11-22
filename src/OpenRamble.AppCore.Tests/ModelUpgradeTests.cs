using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Moving from Parakeet v3 to Parakeet Ultra without a moment of broken dictation.
///
/// The person who updates keeps dictating with the model already on their machine. The new model
/// is offered, downloaded on request, and used once it verifies and loads; only then is the old
/// one deleted.
/// </summary>
/// <remarks>
/// Ported from upstream's <c>ModelUpgradeTests.swift</c>, with this port's lineup: the Linux build
/// replaces its own Parakeet v3 ONNX build with Parakeet Ultra. A build that names no previous
/// model - Windows, for now - behaves exactly as before, which the last test pins.
/// </remarks>
public sealed class ModelUpgradeTests : IDisposable
{
    private readonly AppHarness harness = new()
    {
        Model = ModelManifest.BundledUltra(),
        PreviousModel = ModelManifest.Bundled(),
    };

    public void Dispose() => harness.Dispose();

    private int UltraMegabytes => (int)((ModelManifest.BundledUltra().TotalByteCount + 500_000) / 1_000_000);

    [Fact]
    public async Task AnUpdatedMachineKeepsDictatingWithItsModelAndIsOfferedUltra()
    {
        harness.InstallPreviousModelMarker();
        AppState state = harness.MakeState();

        await state.RefreshModelState();

        Assert.True(state.ModelStateValue.IsReady, "the model already on disk keeps dictation working");
        Assert.IsType<ModelState.NotInstalled>(state.UpgradeState);
        Assert.Equal(UltraMegabytes, state.RemainingDownloadMegabytes);
    }

    [Fact]
    public async Task AFreshMachineIsAskedForUltraAndNoUpgradeIsShown()
    {
        AppState state = harness.MakeState();

        await state.RefreshModelState();

        Assert.IsType<ModelState.NotInstalled>(state.ModelStateValue);
        Assert.Null(state.UpgradeState);
    }

    [Fact]
    public async Task OnceUltraIsInstalledItServesAndTheOldModelIsDeletedAfterItLoads()
    {
        harness.InstallPreviousModelMarker();
        harness.InstallModelMarker();
        var engine = new DirectoryRecordingEngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();

        await state.RefreshModelState();
        await TestWait.For("the engine became ready", () => state.IsEngineReady);

        Assert.Null(state.UpgradeState);
        Assert.All(engine.LoadedDirectories, folder => Assert.Contains("parakeet-ultra", folder, StringComparison.Ordinal));
        await TestWait.For(
            "the replaced model is removed after Ultra loads",
            () => !Directory.Exists(harness.PreviousModelDirectory()));
    }

    [Fact]
    public async Task TheOldModelServesWhileUltraIsOnOfferAndIsKeptUntilThen()
    {
        harness.InstallPreviousModelMarker();
        var engine = new DirectoryRecordingEngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();

        await state.RefreshModelState();
        await TestWait.For("the engine became ready", () => state.IsEngineReady);

        Assert.NotEmpty(engine.LoadedDirectories);
        Assert.All(engine.LoadedDirectories, folder => Assert.Contains("parakeet-tdt-0.6b-v3", folder, StringComparison.Ordinal));
        Assert.True(Directory.Exists(harness.PreviousModelDirectory()));
    }

    /// <summary>
    /// Ultra arriving is a new folder for the next preparation, and the engine loads it: the old
    /// model is not what keeps serving once the new one is ready.
    /// </summary>
    [Fact]
    public async Task UltraArrivingWhileTheOldModelServesMovesRecognitionToUltra()
    {
        harness.InstallPreviousModelMarker();
        var engine = new DirectoryRecordingEngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        await TestWait.For("the old model serves", () => state.IsEngineReady);

        harness.InstallModelMarker();
        await state.RefreshModelState();

        await TestWait.For(
            "Ultra loaded",
            () => state.IsEngineReady && engine.LoadedDirectories[^1].Contains("parakeet-ultra", StringComparison.Ordinal));
        Assert.Null(state.UpgradeState);
        await TestWait.For("the old model removed", () => !Directory.Exists(harness.PreviousModelDirectory()));
    }

    [Fact]
    public async Task DeletingTheModelRemovesBothCopies()
    {
        harness.InstallPreviousModelMarker();
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();

        state.DeleteModel();
        await TestWait.For("both gone", () => state.ModelStateValue is ModelState.NotInstalled);

        Assert.False(Directory.Exists(harness.PreviousModelDirectory()));
    }

    [Fact]
    public void TheUpgradeCardSaysDictationKeepsWorking()
    {
        ModelStatus offer = ModelStatus.Upgrade(new ModelState.NotInstalled(), 740);
        Assert.Equal([ModelStatus.Action.Install], offer.Actions);
        Assert.Equal("Download Model — 740 MB", offer.TitleFor(ModelStatus.Action.Install));

        ModelStatus failed = ModelStatus.Upgrade(new ModelState.Failed(new ModelStoreError.Download("offline")), 740);
        Assert.Equal([ModelStatus.Action.Retry], failed.Actions);
        Assert.Equal(ModelStatus.Tone.Failure, failed.ToneValue);
        Assert.Contains("current model", failed.Detail, StringComparison.Ordinal);

        ModelStatus downloading = ModelStatus.Upgrade(new ModelState.Downloading(370_000_000, 740_000_000), 740);
        Assert.Equal([ModelStatus.Action.Cancel], downloading.Actions);
        Assert.Contains("keeps using the current model", downloading.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ultra's files verify but the engine rejects them. Dictation does not die on a model the
    /// machine still holds: the old one serves, stays on disk, and Ultra is offered again.
    /// </summary>
    [Fact]
    public async Task UltraThatVerifiesButDoesNotLoadLeavesTheOldModelServing()
    {
        harness.InstallPreviousModelMarker();
        harness.InstallModelMarker();
        var engine = new RejectingUltraEngine(new ASREngineError.ModelsUnavailable("bad tensor"));
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();

        await state.RefreshModelState();
        await TestWait.For(
            "the old model serves",
            () => state.IsEngineReady && engine.LoadedDirectories.Any(folder => folder.Contains("parakeet-tdt-0.6b-v3", StringComparison.Ordinal)));

        Assert.True(state.ModelStateValue.IsReady);
        Assert.IsType<ModelState.RepairRequired>(state.UpgradeState);
        Assert.Equal(UltraMegabytes, state.RemainingDownloadMegabytes);
        Assert.True(Directory.Exists(harness.PreviousModelDirectory()));

        // A look at the disk does not move recognition back onto the model that failed.
        await state.RefreshModelState();
        Assert.True(state.IsEngineReady);
        Assert.IsType<ModelState.RepairRequired>(state.UpgradeState);
    }

    /// <summary>
    /// The runtime itself cannot start - a library missing, a processor it does not support, a
    /// build that does not match. New files would not change that, so none are offered.
    /// </summary>
    [Fact]
    public async Task UltraWhoseRuntimeCannotStartLeavesTheOldModelServingAndOffersNoDownload()
    {
        harness.InstallPreviousModelMarker();
        harness.InstallModelMarker();
        var engine = new RejectingUltraEngine(new ASREngineError.RuntimeUnavailable("libtranscribe.so not found"));
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();

        await state.RefreshModelState();
        await TestWait.For(
            "the old model serves",
            () => state.IsEngineReady && engine.LoadedDirectories.Any(folder => folder.Contains("parakeet-tdt-0.6b-v3", StringComparison.Ordinal)));

        Assert.True(state.ModelStateValue.IsReady);
        Assert.Null(state.UpgradeState);
        Assert.Equal(0, state.RemainingDownloadMegabytes);
        Assert.True(Directory.Exists(harness.PreviousModelDirectory()));
        Assert.DoesNotContain("redownload", state.LastNotice?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ultra fails, the old model serves, and then the old model fails too. Neither failure may
    /// erase the other: with one remembered at a time the lineup flipped back to Ultra, every load
    /// came back "overtaken", and the retry ladder reloaded both models forever.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenBothModelsFailToLoadThePreparationStopsAndSaysSo(bool runtime)
    {
        harness.InstallPreviousModelMarker();
        harness.InstallModelMarker();
        harness.EngineWarmupRetryDelay = TimeSpan.FromMilliseconds(5);
        ASREngineError rejection = runtime
            ? new ASREngineError.RuntimeUnavailable("libtranscribe.so not found")
            : new ASREngineError.ModelsUnavailable("bad tensor");
        var engine = new RejectingUltraEngine(rejection, rejectsEverything: true);
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();

        await state.RefreshModelState();
        await TestWait.For(
            "the preparation gave up",
            () => !state.ModelStateValue.IsReady && !state.IsPreparingEngine);
        int attempts = engine.Attempts;
        await Task.Delay(200);

        Assert.Equal(attempts, engine.Attempts);
        Assert.False(state.IsEngineReady);
        if (runtime)
        {
            Assert.IsType<ModelState.Failed>(state.ModelStateValue);
            Assert.DoesNotContain("redownload", state.LastNotice?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.IsType<ModelState.RepairRequired>(state.ModelStateValue);
            Assert.Contains("Neither", state.LastNotice?.Message ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>With nothing to fall back to, a runtime that cannot start is not a repair either.</summary>
    [Fact]
    public async Task ARuntimeThatCannotStartIsNotOfferedAsARepair()
    {
        using var single = new AppHarness();
        single.InstallModelMarker();
        single.WarmUpEngine = new RejectingUltraEngine(new ASREngineError.RuntimeUnavailable("libtranscribe.so not found"), rejectsEverything: true);
        AppState state = single.MakeState();

        await TestWait.For("the load failed", () => state.ModelStateValue is ModelState.Failed);

        Assert.False(state.IsEngineReady);
        Assert.Equal(0, state.RemainingDownloadMegabytes);
        Assert.DoesNotContain("redownload", state.LastNotice?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // A look at the disk keeps saying so.
        await state.RefreshModelState();
        Assert.IsType<ModelState.Failed>(state.ModelStateValue);
    }

    /// <summary>A build that names no previous model never offers an upgrade and never deletes one.</summary>
    [Fact]
    public async Task ABuildWithoutAPreviousModelIsUnchanged()
    {
        using var windows = new AppHarness();
        windows.InstallModelMarker();
        AppState state = windows.MakeState();

        await state.RefreshModelState();

        Assert.True(state.ModelStateValue.IsReady);
        Assert.Null(state.UpgradeState);
    }
}

/// <summary>An engine that loads anything and remembers which folders it was given.</summary>
public sealed class DirectoryRecordingEngine : IASREngineAdapting
{
    private readonly Lock gate = new();
    private readonly List<string> directories = [];

    public IReadOnlyList<string> LoadedDirectories { get { lock (gate) { return [.. directories]; } } }

    public Task LoadModels(string directory)
    {
        lock (gate) { directories.Add(directory); }
        return Task.CompletedTask;
    }

    public Task<ASRResult> Transcribe(float[] samples) =>
        Task.FromResult(new ASRResult(string.Empty, TimeSpan.FromSeconds(1), TimeSpan.Zero));

    public Task Unload() => Task.CompletedTask;
}

/// <summary>An engine that refuses Parakeet Ultra's folder - or every folder - with the given error.</summary>
public sealed class RejectingUltraEngine(ASREngineError rejection, bool rejectsEverything = false) : IASREngineAdapting
{
    private readonly Lock gate = new();
    private readonly List<string> directories = [];

    public IReadOnlyList<string> LoadedDirectories { get { lock (gate) { return [.. directories]; } } }

    /// <summary>Every load asked for, refused or not.</summary>
    public int Attempts => Volatile.Read(ref attempts);

    private int attempts;

    public Task LoadModels(string directory)
    {
        Interlocked.Increment(ref attempts);
        if (rejectsEverything || directory.Contains("parakeet-ultra", StringComparison.Ordinal))
        {
            return Task.FromException(rejection);
        }

        lock (gate) { directories.Add(directory); }
        return Task.CompletedTask;
    }

    public Task<ASRResult> Transcribe(float[] samples) =>
        Task.FromResult(new ASRResult(string.Empty, TimeSpan.FromSeconds(1), TimeSpan.Zero));

    public Task Unload() => Task.CompletedTask;
}
