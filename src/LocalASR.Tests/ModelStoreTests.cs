using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalASR.Tests;

/// <summary>
/// Fake downloader: hands out predefined content, counts calls and knows how to fall over.
/// The real network takes no part in the tests.
/// </summary>
/// <remarks>
/// The Swift original is an <c>actor</c>; here a lock serializes the same bookkeeping.
/// </remarks>
internal sealed class FakeDownloader : IModelDownloading
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> contents;
    private readonly Dictionary<string, Dictionary<string, byte[]>> contentsByHost = [];
    private ModelDownloadError? failure;
    private readonly Dictionary<string, ModelDownloadError> failuresByHost = [];

    /// <summary>
    /// How many more times this host should fail before it starts working.
    ///
    /// A flaky network is not "the mirror is down", it is "the mirror answers on the second
    /// try". Without this the retry path cannot be tested at all.
    /// </summary>
    private readonly Dictionary<string, (int Remaining, ModelDownloadError Error)> transientFailuresByHost = [];

    private readonly Dictionary<string, int> attemptsByHost = [];
    private readonly List<string> requestedPaths = [];

    /// <summary>Hosts in order of access - they show whether the backup address was reached.</summary>
    private readonly List<string> requestedHosts = [];

    public FakeDownloader(Dictionary<string, byte[]> contents)
    {
        this.contents = contents;
    }

    public IReadOnlyDictionary<string, int> AttemptsByHost
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, int>(attemptsByHost);
            }
        }
    }

    public IReadOnlyList<string> RequestedPaths
    {
        get
        {
            lock (gate)
            {
                return [.. requestedPaths];
            }
        }
    }

    public IReadOnlyList<string> RequestedHosts
    {
        get
        {
            lock (gate)
            {
                return [.. requestedHosts];
            }
        }
    }

    public void SetFailure(ModelDownloadError? error)
    {
        lock (gate)
        {
            failure = error;
        }
    }

    /// <summary>Force a specific source to fail: this is how the walk to the next one is checked.</summary>
    public void SetFailure(ModelDownloadError error, string host)
    {
        lock (gate)
        {
            failuresByHost[host] = error;
        }
    }

    /// <summary>Fail this many times in a row on this host, then behave normally.</summary>
    public void SetTransientFailures(int times, string host, ModelDownloadError? error = null)
    {
        lock (gate)
        {
            transientFailuresByHost[host] = (
                times,
                error ?? new ModelDownloadError.Network("A TLS error caused the secure connection to fail."));
        }
    }

    /// <summary>
    /// Slip other content to a source: the sums must catch it no matter where the file came
    /// from.
    /// </summary>
    public void SetContents(Dictionary<string, byte[]> replacement, string host)
    {
        lock (gate)
        {
            contentsByHost[host] = replacement;
        }
    }

    public Task<string> Download(
        Uri url,
        long expectedBytes,
        Action<long> onProgress,
        CancellationToken cancellationToken = default)
    {
        byte[] data;
        lock (gate)
        {
            requestedPaths.Add(url.Segments[^1]);
            string host = url.Host;
            requestedHosts.Add(host);
            attemptsByHost[host] = attemptsByHost.GetValueOrDefault(host) + 1;
            if (failure is not null)
            {
                throw failure;
            }

            if (failuresByHost.TryGetValue(host, out ModelDownloadError? hostFailure))
            {
                throw hostFailure;
            }

            if (transientFailuresByHost.TryGetValue(host, out (int Remaining, ModelDownloadError Error) transient)
                && transient.Remaining > 0)
            {
                transientFailuresByHost[host] = (transient.Remaining - 1, transient.Error);
                throw transient.Error;
            }

            // The key is looked for at the tail of the address - this way the test does not
            // depend on the shape of the link.
            Dictionary<string, byte[]> table = contentsByHost.GetValueOrDefault(host) ?? contents;
            string? key = table.Keys.FirstOrDefault(
                candidate => url.AbsoluteUri.EndsWith(candidate, StringComparison.Ordinal));
            if (key is null)
            {
                throw new ModelDownloadError.HttpStatus(404);
            }

            data = table[key];
        }

        onProgress(data.Length);
        string temporary = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():D}");
        File.WriteAllBytes(temporary, data);
        return Task.FromResult(temporary);
    }
}

public class ModelStoreTests : IDisposable
{
    private readonly string root;

    private static readonly byte[] FileA = Encoding.UTF8.GetBytes("содержимое первого файла");
    private static readonly byte[] FileB = Encoding.UTF8.GetBytes("содержимое второго файла");

    public ModelStoreTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():D}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    internal static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static ModelManifest MakeManifest(bool corruptChecksumForB = false) => new(
        modelID: "test-model",
        repository: "acme/test",
        revision: new string('a', 40),
        fluidAudioVersion: "0.15.5",
        quantization: "test",
        license: "CC-BY-4.0",
        files:
        [
            new ModelManifest.File("Encoder.mlmodelc/weight.bin", FileA.Length, Sha256(FileA)),
            new ModelManifest.File(
                "vocab.json",
                FileB.Length,
                corruptChecksumForB ? new string('b', 64) : Sha256(FileB)),
        ]);

    private (ModelStore Store, ModelInstallLayout Layout) MakeStore(
        ModelManifest manifest,
        IModelDownloading downloader)
    {
        var layout = new ModelInstallLayout(
            root: root,
            modelID: manifest.ModelID,
            revision: manifest.Revision,
            engineFolderName: "repo");
        return (new ModelStore(manifest, layout, downloader), layout);
    }

    private static FakeDownloader WorkingDownloader() =>
        new(new Dictionary<string, byte[]> { ["weight.bin"] = FileA, ["vocab.json"] = FileB });

    [Fact]
    public async Task InstallsAndBecomesReady()
    {
        ModelManifest manifest = MakeManifest();
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest, WorkingDownloader());

        await store.Install();

        ModelState state = store.CurrentState();
        Assert.True(state.IsReady, $"expected a ready state, got {state}");
        Assert.True(File.Exists(layout.ReadyMarker));
        // The files sit inside a folder whose name the library's loader expects.
        string encoder = Path.Combine(layout.EngineDirectory, "Encoder.mlmodelc", "weight.bin");
        Assert.Equal(FileA, File.ReadAllBytes(encoder));
    }

    [Fact]
    public async Task CorruptedFileLeavesNothingInstalled()
    {
        // The sum of the second file in the manifest will certainly not add up.
        ModelManifest manifest = MakeManifest(corruptChecksumForB: true);
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest, WorkingDownloader());

        await store.Install();

        ModelState state = store.CurrentState();
        var failed = Assert.IsType<ModelState.Failed>(state);
        Assert.IsType<ModelStoreError.Verification>(failed.Error);
        // The main thing: no half-installed model is left behind.
        Assert.False(Directory.Exists(layout.InstalledDirectory));
        Assert.False(File.Exists(layout.ReadyMarker));
    }

    [Fact]
    public async Task NetworkFailureLeavesNoStagingBehind()
    {
        ModelManifest manifest = MakeManifest();
        var downloader = new FakeDownloader([]);
        downloader.SetFailure(new ModelDownloadError.Network("the network is unreachable"));
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest, downloader);

        await store.Install();

        Assert.IsType<ModelState.Failed>(store.CurrentState());
        Assert.DoesNotContain(
            Directory.Exists(layout.ModelDirectory) ? Directory.GetDirectories(layout.ModelDirectory) : [],
            entry => Path.GetFileName(entry).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshRejectsMarkerFromAnotherRevision()
    {
        ModelManifest manifest = MakeManifest();
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest, WorkingDownloader());
        await store.Install();

        // Put a marker from another revision in place - the installation must stop being
        // considered suitable, and not "probably suitable".
        var alien = new ModelReadyMarker(
            new string('f', 40),
            "0.15.5",
            manifest.Files.Count,
            manifest.TotalByteCount,
            DateTimeOffset.UtcNow);
        File.WriteAllBytes(
            layout.ReadyMarker,
            JsonSerializer.SerializeToUtf8Bytes(alien, ModelStore.MarkerOptions));

        Assert.IsType<ModelState.RepairRequired>(store.RefreshState());
    }

    [Fact]
    public async Task RefreshRequiresReadyMarkerWhenModelDirectoryExists()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        File.Delete(layout.ReadyMarker);

        Assert.IsType<ModelState.RepairRequired>(store.RefreshState());
    }

    [Fact]
    public async Task RefreshRejectsCorruptedReadyMarker()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        File.WriteAllText(layout.ReadyMarker, "not-json");

        Assert.IsType<ModelState.RepairRequired>(store.RefreshState());
    }

    [Fact]
    public async Task RefreshRequiresEveryInstalledFile()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        File.Delete(Path.Combine(layout.EngineDirectory, "vocab.json"));

        Assert.IsType<ModelState.RepairRequired>(store.RefreshState());
    }

    [Fact]
    public async Task RefreshRejectsHiddenFileOutsideManifestInventory()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        File.WriteAllText(Path.Combine(layout.EngineDirectory, ".hidden"), "unexpected");

        Assert.IsType<ModelState.RepairRequired>(store.RefreshState());
    }

    /// <summary>
    /// Finder creates <c>.DS_Store</c> when a folder is merely browsed. That is litter, not
    /// damage: declaring a repair and transferring 483 MB because of it is impossible.
    /// </summary>
    [Fact]
    public async Task Scenario001()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        string dsStore = Path.Combine(layout.EngineDirectory, ".DS_Store");
        File.WriteAllBytes(dsStore, [0x00, 0x00, 0x00, 0x01]);

        ModelState state = store.RefreshState();

        Assert.True(state.IsReady, $"browsing a folder is no reason to re-download the model: {state}");
        Assert.False(File.Exists(dsStore), "the Finder litter must be removed, or it brings repair back");
    }

    [Fact]
    public async Task RefreshDetectsSameSizeMutation()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        string target = Path.Combine(layout.EngineDirectory, "vocab.json");
        File.WriteAllBytes(target, Enumerable.Repeat((byte)0x78, FileB.Length).ToArray());
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(10));

        Assert.IsType<ModelState.RepairRequired>(store.RefreshState());
    }

    [Fact]
    public async Task RefreshRestoresInterruptedPromotionBackup()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        Directory.Move(layout.InstalledDirectory, layout.BackupDirectory);

        ModelState state = store.RefreshState();

        Assert.True(state.IsReady);
        Assert.True(File.Exists(layout.ReadyMarker));
        Assert.False(Directory.Exists(layout.BackupDirectory));
    }

    [Fact]
    public async Task EveryPromotionInterruptionRecoversOldOrNewReadyModel()
    {
        foreach (ModelPromotionCheckpoint checkpoint in Enum.GetValues<ModelPromotionCheckpoint>())
        {
            string caseRoot = Path.Combine(root, checkpoint.ToString());
            Directory.CreateDirectory(caseRoot);
            ModelManifest manifest = MakeManifest();
            FakeDownloader downloader = WorkingDownloader();
            var layout = new ModelInstallLayout(
                root: caseRoot,
                modelID: manifest.ModelID,
                revision: manifest.Revision,
                engineFolderName: "repo");

            var baseline = new ModelStore(manifest, layout, downloader);
            await baseline.Install();
            Assert.True(baseline.CurrentState().IsReady, $"baseline {checkpoint}");

            var interrupted = new ModelStore(
                manifest,
                layout,
                downloader,
                injectedPromotionFailure: checkpoint);
            await interrupted.Install();
            ModelState interruptedState = interrupted.CurrentState();
            if (checkpoint == ModelPromotionCheckpoint.AfterBackupRemoval)
            {
                // Past this point the model is installed and marked ready, and only the old
                // copy is left to sweep up. A sweep that fails is not a failed install:
                // reporting it as one skipped the engine warm-up and, on the next launch,
                // offered redownloading 483 MB as the sole remedy for a working model.
                Assert.True(
                    interruptedState.IsReady,
                    $"housekeeping after the ready marker must not fail the install: {interruptedState}");
            }
            else
            {
                Assert.IsType<ModelState.Failed>(interruptedState);
            }

            var relaunched = new ModelStore(manifest, layout, downloader);
            ModelState recovered = relaunched.RefreshState();
            Assert.True(
                recovered.IsReady,
                $"after {checkpoint} either the old or the new model must be ready: {recovered}");
            Assert.False(Directory.Exists(layout.BackupDirectory));
        }
    }

    [Fact]
    public async Task DeleteRemovesEverything()
    {
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), WorkingDownloader());
        await store.Install();
        Assert.True(store.CurrentState().IsReady);

        await store.Delete();

        Assert.Equal(new ModelState.NotInstalled(), store.CurrentState());
        Assert.False(Directory.Exists(layout.ModelDirectory));
    }

    [Fact]
    public async Task InstallIsSkippedWhenAlreadyReady()
    {
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, _) = MakeStore(MakeManifest(), downloader);

        await store.Install();
        int firstRoundRequests = downloader.RequestedPaths.Count;

        await store.Install();
        int secondRoundRequests = downloader.RequestedPaths.Count;

        Assert.Equal(firstRoundRequests, secondRoundRequests);
    }

    [Fact]
    public async Task ExplicitRepairReinstallsEvenWhenStoreWasReady()
    {
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, _) = MakeStore(MakeManifest(), downloader);
        await store.Install();
        int firstRoundRequests = downloader.RequestedPaths.Count;

        await store.Repair();

        Assert.True(store.CurrentState().IsReady);
        Assert.Equal(firstRoundRequests * 2, downloader.RequestedPaths.Count);
    }

    /// <summary>
    /// Updating FluidAudio must not cost the user a model download.
    ///
    /// The model revision is a pinned commit SHA; if it has not changed, the disk holds
    /// exactly the bytes the new manifest asks for. The marker used to be checked together
    /// with the library version, and a dependency patch turned into "the model is damaged,
    /// download 483 MB again" for everyone who updated the application. Now this is a local
    /// re-check of the sums and a rewritten marker.
    /// </summary>
    [Fact]
    public async Task FluidAudioBumpRevalidatesLocallyWithoutDownloading()
    {
        ModelManifest installed = MakeManifest();
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, ModelInstallLayout layout) = MakeStore(installed, downloader);
        await store.Install();
        Assert.True(store.CurrentState().IsReady);
        int requestsAfterInstall = downloader.RequestedPaths.Count;

        // The library was updated, the weights were not touched: the same revision, the same
        // set of files, another version of FluidAudio.
        var bumped = new ModelManifest(
            installed.ModelID,
            installed.Repository,
            installed.Revision,
            "0.15.6",
            installed.Quantization,
            installed.License,
            installed.Files);
        var upgraded = new ModelStore(bumped, layout, downloader);

        ModelState state = upgraded.RefreshState();

        Assert.True(state.IsReady, $"intact files after a library bump are Ready, not repair: {state}");
        Assert.Equal(requestsAfterInstall, downloader.RequestedPaths.Count);
        // The marker has been rewritten for the new version - otherwise the full re-check of
        // the sums would repeat on every application launch.
        ModelReadyMarker marker = JsonSerializer.Deserialize<ModelReadyMarker>(
            File.ReadAllBytes(layout.ReadyMarker),
            ModelStore.MarkerOptions)!;
        Assert.Equal("0.15.6", marker.FluidAudioVersion);
        Assert.True(marker.Matches(bumped));
    }

    /// <summary>
    /// The other side of the same edit: a changed revision means different weights, so here
    /// the download must happen rather than quietly pass for a suitable installation.
    /// </summary>
    [Fact]
    public async Task DifferentRevisionStillNeedsInstall()
    {
        ModelManifest installed = MakeManifest();
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, _) = MakeStore(installed, downloader);
        await store.Install();

        var newRevision = new ModelManifest(
            installed.ModelID,
            installed.Repository,
            new string('c', 40),
            installed.FluidAudioVersion,
            installed.Quantization,
            installed.License,
            installed.Files);
        var layout = new ModelInstallLayout(
            root: root,
            modelID: newRevision.ModelID,
            revision: newRevision.Revision,
            engineFolderName: "repo");
        var other = new ModelStore(newRevision, layout, downloader);

        Assert.IsType<ModelState.NotInstalled>(other.RefreshState());
    }

    /// <summary>
    /// Damage stays damage: a version bump must not turn into "we will not check, it will do".
    /// </summary>
    [Fact]
    public async Task FluidAudioBumpStillCatchesDamagedFile()
    {
        ModelManifest installed = MakeManifest();
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, ModelInstallLayout layout) = MakeStore(installed, downloader);
        await store.Install();

        // The file was replaced after the installation - the same size, different content.
        string victim = Path.Combine(layout.EngineDirectory, "Encoder.mlmodelc", "weight.bin");
        File.WriteAllBytes(victim, Encoding.UTF8.GetBytes("содержимое ПЕРВОГО файла"));

        var bumped = new ModelManifest(
            installed.ModelID,
            installed.Repository,
            installed.Revision,
            "0.15.6",
            installed.Quantization,
            installed.License,
            installed.Files);
        var upgraded = new ModelStore(bumped, layout, downloader);

        Assert.IsType<ModelState.RepairRequired>(upgraded.RefreshState());
    }

    [Fact]
    public void ProgressIsReportedWhileDownloading()
    {
        Assert.Equal(0.25, new ModelState.Downloading(50, 200).Progress);
        Assert.Equal(0.25, new ModelState.Verifying(1, 4).Progress);
        Assert.Null(new ModelState.NotInstalled().Progress);
    }
}
