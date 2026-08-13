namespace LocalASR.Tests;

/// <summary>
/// The layout decides where the 483 MB of downloaded files will go.
///
/// The path of each file comes from the manifest, and the manifest is data. A mistake here
/// means writing outside our own directory: into someone else's settings, into autostart,
/// into the neighbouring model's installation. The manifest already rejects such paths, but
/// the layout must hold the barrier on its own - that is why it is the second one.
/// </summary>
public class ModelInstallLayoutTests
{
    private readonly string directory = Path.Combine("/tmp", "openramble-tests", "parakeet");
    private readonly ModelInstallLayout layout = new(
        root: Path.Combine("/tmp", "openramble-tests"),
        modelID: "parakeet",
        revision: new string('a', 40),
        engineFolderName: "parakeet-tdt-0.6b-v3");

    private static ModelManifest.File File(string path) => new(path, 1, new string('0', 64));

    // MARK: - Exiting directory boundaries

    [Fact]
    public void RefusesPathClimbingOutOfTheDirectory()
    {
        // "../" in a path is an attempt to write a file past the installation. The refusal
        // must be explicit, and not "well, let us put it wherever it lands".
        foreach (string path in new[] { "../захвачено.bin", "вложенная/../../захвачено.bin", "../../../../etc/захвачено" })
        {
            var error = Assert.Throws<ModelInstallError.UnsafePath>(
                () => layout.Destination(File(path), directory));
            Assert.Equal(path, error.Path);
        }
    }

    [Fact]
    public void RefusesSiblingDirectoryThatSharesTheNamePrefix()
    {
        // The least obvious case: "/tmp/…/parakeet" is a string prefix of
        // "/tmp/…/parakeet-fake", so a "starts with" check let this way out through.
        // Comparison by path components catches it.
        const string Traversal = "../parakeet-подделка/weights.bin";

        var error = Assert.Throws<ModelInstallError.UnsafePath>(
            () => layout.Destination(File(Traversal), directory));
        Assert.Equal(Traversal, error.Path);
    }

    [Fact]
    public void RefusesEmptyPath()
    {
        // An empty path points at the directory itself: writing to it would overwrite the
        // installation folder with a file.
        var error = Assert.Throws<ModelInstallError.UnsafePath>(
            () => layout.Destination(File(string.Empty), directory));
        Assert.Equal(string.Empty, error.Path);
    }

    [Fact]
    public void AbsolutePathIsRefused()
    {
        // Previously the leading slash was silently glued inside the installation:
        // "/etc/passwd" became a file inside the model folder. It did not lead outside, but
        // such a path cannot be in the manifest - which means the manifest is corrupt, and we
        // need to know about that right away rather than find an incomprehensible file on
        // disk.
        Assert.Throws<ModelInstallError.UnsafePath>(
            () => layout.Destination(File("/etc/passwd"), directory));
    }

    [Fact]
    public void KeepsNestedPathsFromTheManifest()
    {
        // The common case: model files live in nested bundles.
        string destination = layout.Destination(File("Encoder.mlmodelc/weights/weight.bin"), directory);

        Assert.Equal(
            Path.Combine(directory, "Encoder.mlmodelc", "weights", "weight.bin"),
            destination);
    }

    // MARK: - Installation paths

    [Fact]
    public void RevisionIsPartOfTheInstalledPath()
    {
        // A revision in the folder name is what allows installing a new model without
        // breaking the old one that already works.
        Assert.EndsWith(new string('a', 40), layout.InstalledDirectory, StringComparison.Ordinal);
        Assert.Equal("parakeet-tdt-0.6b-v3", Path.GetFileName(layout.EngineDirectory));
        Assert.Equal(".ready.json", Path.GetFileName(layout.ReadyMarker));
    }

    [Fact]
    public void StagingDirectoriesOfTwoAttemptsDoNotCollide()
    {
        // Two installation attempts must not write into the same folder: the second would
        // overwrite the files of the first and the result would be an installation made of
        // pieces of different downloads.
        string first = layout.StagingDirectory(Guid.NewGuid());
        string second = layout.StagingDirectory(Guid.NewGuid());

        Assert.NotEqual(first, second);
        Assert.StartsWith(".staging-", Path.GetFileName(first), StringComparison.Ordinal);
    }

    [Fact]
    public void EngineFolderNameDropsTheCoremlSuffix()
    {
        // The library derives the folder name from the repository name without "-coreml".
        // A mistake here means "the model is installed, but the engine does not find it".
        var manifest = new ModelManifest(
            modelID: "parakeet",
            repository: "FluidInference/parakeet-tdt-0.6b-v3-coreml",
            revision: new string('b', 40),
            fluidAudioVersion: "0.15.5",
            quantization: "int8",
            license: "CC-BY-4.0",
            files: [File("Encoder.mlmodelc/weight.bin")]);

        var derived = new ModelInstallLayout(manifest, root: Path.Combine("/tmp", "x"));

        Assert.Equal("parakeet-tdt-0.6b-v3", Path.GetFileName(derived.EngineDirectory));
    }
}

/// <summary>
/// The ready marker is the only thing that distinguishes a working installation from a folder
/// with files in it. If it agrees to someone else's data, the user gets "the model is
/// installed" and a crash at the moment of the first dictation.
/// </summary>
public class ModelReadyMarkerTests
{
    private readonly ModelManifest manifest = new(
        modelID: "parakeet",
        repository: "acme/parakeet-coreml",
        revision: new string('c', 40),
        fluidAudioVersion: "0.15.5",
        quantization: "int8",
        license: "CC-BY-4.0",
        files:
        [
            new ModelManifest.File("a.bin", 10, new string('0', 64)),
            new ModelManifest.File("b.bin", 20, new string('1', 64)),
        ]);

    [Fact]
    public void MatchesItsOwnManifest()
    {
        var marker = new ModelReadyMarker(manifest, DateTimeOffset.UtcNow);

        Assert.True(marker.Matches(manifest));
    }

    [Fact]
    public void RejectsInstallationMadeForAnotherFluidAudioVersion()
    {
        // The files are the same, but the engine is different: the library changes the
        // expected layout between versions, and "probably will work" is unacceptable here.
        var marker = new ModelReadyMarker(
            manifest.Revision,
            "0.14.0",
            manifest.Files.Count,
            manifest.TotalByteCount,
            DateTimeOffset.UtcNow);

        Assert.False(marker.Matches(manifest));
    }

    [Fact]
    public void RejectsMarkerWithWrongFileCountOrSize()
    {
        // A discrepancy in the number of files or in the total size means the installation is
        // incomplete - half the model is on disk.
        var fewerFiles = new ModelReadyMarker(
            manifest.Revision,
            manifest.FluidAudioVersion,
            manifest.Files.Count - 1,
            manifest.TotalByteCount,
            DateTimeOffset.UtcNow);
        var smaller = new ModelReadyMarker(
            manifest.Revision,
            manifest.FluidAudioVersion,
            manifest.Files.Count,
            manifest.TotalByteCount - 1,
            DateTimeOffset.UtcNow);

        Assert.False(fewerFiles.Matches(manifest));
        Assert.False(smaller.Matches(manifest));
    }
}

/// <summary>
/// The barrier must not depend on what is already on disk.
///
/// That is where it broke down: bringing a path to canonical form asks the file system, and
/// it answers "/tmp" about an existing folder and "/private/tmp" about a file in it that does
/// not exist yet. The whole installation fell over on the first legal file.
/// </summary>
public class ModelInstallLayoutFilesystemIndependenceTests
{
    private static ModelInstallLayout MakeLayout(string root) =>
        new(root, modelID: "parakeet-tdt-0.6b-v3", revision: "abc123", engineFolderName: "parakeet-tdt-0.6b-v3");

    private static ModelManifest.File File(string path) => new(path, 1, new string('0', 64));

    [Fact]
    public void LegitimatePathIsAcceptedWhenDirectoryAlreadyExists()
    {
        // This is exactly what happens in life: the installation creates a folder, then puts
        // files into it.
        string root = Path.Combine(Path.GetTempPath(), $"layout-{Guid.NewGuid():D}");
        ModelInstallLayout layout = MakeLayout(root);
        string engine = layout.EngineDirectoryInside(layout.StagingDirectory(Guid.NewGuid()));
        Directory.CreateDirectory(engine);
        try
        {
            string destination = layout.Destination(File("Encoder.mlmodelc/weights/weight.bin"), engine);

            Assert.EndsWith(
                Path.Combine("parakeet-tdt-0.6b-v3", "Encoder.mlmodelc", "weights", "weight.bin"),
                destination,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VerdictIsTheSameWhetherTheDirectoryExistsOrNot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"layout-{Guid.NewGuid():D}");
        ModelInstallLayout layout = MakeLayout(root);
        string engine = layout.EngineDirectoryInside(layout.StagingDirectory(Guid.NewGuid()));
        try
        {
            string before = layout.Destination(File("Decoder.mlmodelc/metadata.json"), engine);
            Directory.CreateDirectory(engine);
            string after = layout.Destination(File("Decoder.mlmodelc/metadata.json"), engine);

            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WayOutIsStillRefused()
    {
        string root = Path.Combine(Path.GetTempPath(), $"layout-{Guid.NewGuid():D}");
        ModelInstallLayout layout = MakeLayout(root);
        string engine = layout.EngineDirectoryInside(layout.StagingDirectory(Guid.NewGuid()));
        Directory.CreateDirectory(engine);
        try
        {
            foreach (string path in new[] { "../parakeet-подделка/weights.bin", "/etc/passwd", "a// b", "", "./x", "a/../../b" })
            {
                Assert.Throws<ModelInstallError.UnsafePath>(
                    () => layout.Destination(File(path), engine));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
