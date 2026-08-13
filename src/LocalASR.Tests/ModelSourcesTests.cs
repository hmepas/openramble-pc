using System.Text;
using System.Text.Json.Nodes;

namespace LocalASR.Tests;

/// <summary>
/// Two independent paths to the model: a spare address and an import from a ready-made
/// folder.
///
/// The meaning of both is the same: a single source is a single point of failure. If the
/// repository is deleted or closed for a region, a new user will not launch the application
/// at all. The trust check does not change one step: the SHA-256 sum from the manifest is
/// computed the same way for any origin of the file.
/// </summary>
public class ModelSourcesTests : IDisposable
{
    private readonly string root;
    private readonly string source;

    private static readonly byte[] FileA = Encoding.UTF8.GetBytes("содержимое первого файла");
    private static readonly byte[] FileB = Encoding.UTF8.GetBytes("содержимое второго файла");
    private const string PrimaryHost = "huggingface.co";
    private const string MirrorHost = "github.com";

    public ModelSourcesTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"sources-{Guid.NewGuid():D}");
        source = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():D}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(source);
    }

    public void Dispose()
    {
        foreach (string directory in new[] { root, source })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    private static ModelManifest MakeManifest(
        bool withMirror = true,
        bool corruptChecksumForB = false,
        string pathForB = "vocab.json") => new(
        modelID: "test-model",
        repository: "acme/test",
        revision: new string('a', 40),
        fluidAudioVersion: "0.15.5",
        quantization: "test",
        license: "CC-BY-4.0",
        files:
        [
            new ModelManifest.File("Encoder.mlmodelc/weight.bin", FileA.Length, ModelStoreTests.Sha256(FileA)),
            new ModelManifest.File(
                pathForB,
                FileB.Length,
                corruptChecksumForB ? new string('b', 64) : ModelStoreTests.Sha256(FileB)),
        ],
        mirrorSource: withMirror
            ? new ModelManifest.Mirror("mikwiseman/openramble", "models-test")
            : null);

    private (ModelStore Store, ModelInstallLayout Layout) MakeStore(
        ModelManifest manifest,
        IModelDownloading? downloader = null)
    {
        var layout = new ModelInstallLayout(
            root: root,
            modelID: manifest.ModelID,
            revision: manifest.Revision,
            engineFolderName: "repo");
        return (new ModelStore(manifest, layout, downloader ?? new FakeDownloader([])), layout);
    }

    private static FakeDownloader WorkingDownloader() =>
        new(new Dictionary<string, byte[]> { ["weight.bin"] = FileA, ["vocab.json"] = FileB });

    /// <summary>Lay the files out the way an installed model on another machine gives them.</summary>
    private void FillSource(ModelManifest manifest, Dictionary<string, byte[]> contents)
    {
        foreach (ModelManifest.File file in manifest.Files)
        {
            if (!contents.TryGetValue(file.Path, out byte[]? data))
            {
                continue;
            }

            string path = Path.Combine(source, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }

    // MARK: - Spare source

    [Fact]
    public void ManifestBuildsBothAddresses()
    {
        ModelManifest manifest = MakeManifest();
        ModelManifest.File file = manifest.Files[0];

        IReadOnlyList<Uri> addresses = manifest.DownloadURLs(file);

        Assert.Equal(2, addresses.Count);
        Assert.Equal(PrimaryHost, addresses[0].Host);
        Assert.Equal(MirrorHost, addresses[1].Host);
        // GitHub does not allow forward slashes in an attachment name - the path is flattened.
        Assert.Equal("Encoder.mlmodelc__weight.bin", addresses[1].Segments[^1]);
    }

    [Fact]
    public async Task FallsBackToMirrorWhenPrimaryIsGone()
    {
        ModelManifest manifest = MakeManifest();
        FakeDownloader downloader = WorkingDownloader();
        // The repository was deleted - exactly the scenario all of this was done for.
        downloader.SetFailure(new ModelDownloadError.HttpStatus(404), PrimaryHost);
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest, downloader);

        await store.Install();

        ModelState state = store.CurrentState();
        Assert.True(state.IsReady, $"the spare source must pull the install through, got {state}");
        IReadOnlyList<string> hosts = downloader.RequestedHosts;
        Assert.Equal(PrimaryHost, hosts[0]);
        Assert.Contains(MirrorHost, hosts);
        Assert.True(File.Exists(layout.ReadyMarker));
    }

    [Fact]
    public async Task MirrorIsNotTouchedWhenPrimaryWorks()
    {
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, _) = MakeStore(MakeManifest(), downloader);

        await store.Install();

        Assert.DoesNotContain(MirrorHost, downloader.RequestedHosts);
    }

    [Fact]
    public async Task BothSourcesFailingNamesBoth()
    {
        var downloader = new FakeDownloader([]);
        downloader.SetFailure(new ModelDownloadError.HttpStatus(404), PrimaryHost);
        downloader.SetFailure(new ModelDownloadError.Network("the host is unreachable"), MirrorHost);
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), downloader);

        await store.Install();

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        var download = Assert.IsType<ModelStoreError.Download>(failed.Error);
        Assert.Contains(PrimaryHost, download.Reason, StringComparison.Ordinal);
        Assert.Contains(MirrorHost, download.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(layout.InstalledDirectory));
    }

    /// <summary>
    /// A source failing and the user cancelling are two different things. After a "cancel",
    /// walking the mirrors means downloading half a gigabyte in spite of a direct command.
    /// </summary>
    [Fact]
    public async Task CancellationDoesNotWalkToTheMirror()
    {
        FakeDownloader downloader = WorkingDownloader();
        downloader.SetFailure(new ModelDownloadError.Cancelled(), PrimaryHost);
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), downloader);

        await store.Install();

        Assert.Equal(new ModelState.NotInstalled(), store.CurrentState());
        Assert.DoesNotContain(MirrorHost, downloader.RequestedHosts);
        Assert.False(File.Exists(layout.ReadyMarker));
    }

    /// <summary>
    /// The backup address weakens nothing: a file from it is checked by the same sums.
    /// Otherwise the mirror would become a hole in the trust.
    /// </summary>
    [Fact]
    public async Task MirrorContentIsCheckedByTheSameChecksums()
    {
        FakeDownloader downloader = WorkingDownloader();
        downloader.SetFailure(new ModelDownloadError.HttpStatus(404), PrimaryHost);
        downloader.SetContents(
            new Dictionary<string, byte[]>
            {
                ["weight.bin"] = FileA,
                ["vocab.json"] = Encoding.UTF8.GetBytes("подменённое содержимое"),
            },
            MirrorHost);
        (ModelStore store, ModelInstallLayout layout) = MakeStore(MakeManifest(), downloader);

        await store.Install();

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        Assert.IsType<ModelStoreError.Verification>(failed.Error);
        Assert.False(Directory.Exists(layout.InstalledDirectory));
    }

    [Fact]
    public void ManifestWithoutMirrorHasSingleAddress()
    {
        ModelManifest manifest = MakeManifest(withMirror: false);

        Assert.Single(manifest.DownloadURLs(manifest.Files[0]));
        Assert.Null(manifest.MirrorURL(manifest.Files[0]));
    }

    [Fact]
    public void MalformedMirrorIsRejectedAtDecode()
    {
        ModelManifest manifest = MakeManifest();
        var json = new JsonObject
        {
            ["modelID"] = manifest.ModelID,
            ["repository"] = manifest.Repository,
            ["revision"] = manifest.Revision,
            ["fluidAudioVersion"] = manifest.FluidAudioVersion,
            ["quantization"] = manifest.Quantization,
            ["license"] = manifest.License,
            ["files"] = new JsonArray(manifest.Files.Select(file => (JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["byteCount"] = file.ByteCount,
                ["sha256"] = file.Sha256,
            }).ToArray()),
            ["mirror"] = new JsonObject
            {
                ["repository"] = "простоимя",
                ["releaseTag"] = "models-test",
            },
        };

        Assert.Throws<ModelManifestError.Invalid>(
            () => ModelManifest.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    /// <summary>
    /// <b>Known gap.</b> The Core ML manifest carried a second address, and this test used to
    /// require one. The ONNX file set has none yet: a mirror is per-file assets on a GitHub
    /// release someone lays out by hand, and nobody has laid one out for these four files -
    /// upstream publishes them only as a single tar.bz2, which this scheme cannot address.
    /// So the test guards what is true today, in the shape that will fail loudly the moment a
    /// mirror is added without the rest of the plumbing: every file must have at least one
    /// reachable source, and every source the manifest does name must be usable.
    /// </summary>
    [Fact]
    public void BundledManifestOffersEverySourceItNames()
    {
        ModelManifest manifest = ModelManifest.Bundled();

        int expected = manifest.MirrorSource is null ? 1 : 2;
        if (manifest.MirrorSource is { } mirror)
        {
            Assert.Equal(2, mirror.Repository.Split('/').Length);
        }

        Assert.All(
            manifest.Files,
            file => Assert.Equal(expected, manifest.DownloadURLs(file).Count));
    }

    // MARK: - Import from a folder

    [Fact]
    public async Task ImportInstallsFromFolder()
    {
        ModelManifest manifest = MakeManifest();
        FillSource(manifest, new Dictionary<string, byte[]>
        {
            ["Encoder.mlmodelc/weight.bin"] = FileA,
            ["vocab.json"] = FileB,
        });
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest);

        await store.ImportModel(source);

        ModelState state = store.CurrentState();
        Assert.True(state.IsReady, $"importing a ready folder must end with an installation, got {state}");
        string installed = Path.Combine(layout.EngineDirectory, "Encoder.mlmodelc", "weight.bin");
        Assert.Equal(FileA, File.ReadAllBytes(installed));
        Assert.True(File.Exists(layout.ReadyMarker));
    }

    [Fact]
    public async Task ImportNeverGoesToTheNetwork()
    {
        ModelManifest manifest = MakeManifest();
        FillSource(manifest, new Dictionary<string, byte[]>
        {
            ["Encoder.mlmodelc/weight.bin"] = FileA,
            ["vocab.json"] = FileB,
        });
        var downloader = new FakeDownloader([]);
        (ModelStore store, _) = MakeStore(manifest, downloader);

        await store.ImportModel(source);

        Assert.Empty(downloader.RequestedPaths);
    }

    [Fact]
    public async Task ImportWithOneBadChecksumInstallsNothing()
    {
        // The sum of the second file in the manifest will certainly not add up.
        ModelManifest manifest = MakeManifest(corruptChecksumForB: true);
        FillSource(manifest, new Dictionary<string, byte[]>
        {
            ["Encoder.mlmodelc/weight.bin"] = FileA,
            ["vocab.json"] = FileB,
        });
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest);

        await store.ImportModel(source);

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        Assert.IsType<ModelStoreError.Verification>(failed.Error);
        Assert.False(Directory.Exists(layout.InstalledDirectory));
        string[] leftovers = Directory.Exists(layout.ModelDirectory)
            ? Directory.GetDirectories(layout.ModelDirectory)
            : [];
        Assert.DoesNotContain(
            leftovers,
            entry => Path.GetFileName(entry).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportFromFolderMissingAFileFails()
    {
        ModelManifest manifest = MakeManifest();
        FillSource(manifest, new Dictionary<string, byte[]> { ["Encoder.mlmodelc/weight.bin"] = FileA });
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest);

        await store.ImportModel(source);

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        var importSource = Assert.IsType<ModelStoreError.ImportSource>(failed.Error);
        Assert.Contains("vocab.json", importSource.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(layout.InstalledDirectory));
    }

    [Fact]
    public async Task ImportLeavesTheSourceFolderUntouched()
    {
        ModelManifest manifest = MakeManifest();
        FillSource(manifest, new Dictionary<string, byte[]>
        {
            ["Encoder.mlmodelc/weight.bin"] = FileA,
            ["vocab.json"] = FileB,
        });
        (ModelStore store, _) = MakeStore(manifest);

        await store.ImportModel(source);

        // The folder is someone else's: files may not be taken out of it, only copied.
        foreach (ModelManifest.File file in manifest.Files)
        {
            Assert.True(
                File.Exists(Path.Combine(source, file.Path)),
                $"{file.Path} disappeared from the source folder");
        }
    }

    /// <summary>
    /// The path barrier for someone else's folder is needed just as strictly: a manifest with
    /// ".." must not be able to pull a file from anywhere on the disk.
    /// </summary>
    [Fact]
    public async Task ImportRefusesPathLeavingTheFolder()
    {
        ModelManifest manifest = MakeManifest(pathForB: "../снаружи.json");
        FillSource(manifest, new Dictionary<string, byte[]> { ["Encoder.mlmodelc/weight.bin"] = FileA });
        string outside = Path.Combine(Path.GetDirectoryName(source)!, "снаружи.json");
        File.WriteAllBytes(outside, FileB);
        try
        {
            (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest);

            await store.ImportModel(source);

            var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
            var importSource = Assert.IsType<ModelStoreError.ImportSource>(failed.Error);
            Assert.Contains("outside the folder", importSource.Reason, StringComparison.Ordinal);
            Assert.False(Directory.Exists(layout.InstalledDirectory));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// A symbolic link can be repointed after its sum is checked - the installed model would
    /// hold a pointer outside instead of a file.
    /// </summary>
    [Fact]
    public async Task ImportRefusesSymbolicLink()
    {
        ModelManifest manifest = MakeManifest();
        FillSource(manifest, new Dictionary<string, byte[]> { ["Encoder.mlmodelc/weight.bin"] = FileA });
        string elsewhere = Path.Combine(source, "настоящий.json");
        File.WriteAllBytes(elsewhere, FileB);
        File.CreateSymbolicLink(Path.Combine(source, "vocab.json"), elsewhere);
        (ModelStore store, ModelInstallLayout layout) = MakeStore(manifest);

        await store.ImportModel(source);

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        var importSource = Assert.IsType<ModelStoreError.ImportSource>(failed.Error);
        Assert.Contains("symbolic link", importSource.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(layout.InstalledDirectory));
    }

    [Fact]
    public async Task ImportIsSkippedWhenModelIsAlreadyInstalled()
    {
        FakeDownloader downloader = WorkingDownloader();
        (ModelStore store, _) = MakeStore(MakeManifest(), downloader);
        await store.Install();
        Assert.True(store.CurrentState().IsReady);

        // The folder is empty: if the import does start, the installation breaks.
        await store.ImportModel(source);

        ModelState state = store.CurrentState();
        Assert.True(state.IsReady, $"an import must not touch a ready installation: {state}");
    }
}
