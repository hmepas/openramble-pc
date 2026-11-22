namespace LocalASR.Tests;

/// <summary>
/// Checks the real manifest that will go to the user.
///
/// These tests guard not the code but the data: if the manifest generator one day brings the
/// wrong set of files or lands on another revision, the build should fail here, and not for
/// the user after 670 MB of download.
/// </summary>
public class BundledManifestTests
{
    [Fact]
    public void BundledManifestIsValid()
    {
        // The very fact of successful parsing means that all decoder checks have passed:
        // full revision, non-zero sizes, correct sums, safe paths.
        ModelManifest manifest = ModelManifest.Bundled();

        Assert.Equal("parakeet-tdt-0.6b-v3", manifest.ModelID);
        Assert.Equal("hmepas/parakeet-tdt-0.6b-v3-int4", manifest.Repository);
        Assert.Equal("07b96facdcefe49b48403fd372d79b79bbcf6be9", manifest.Revision);
        Assert.Equal("1.13.5", manifest.FluidAudioVersion);
        Assert.Equal("CC-BY-4.0", manifest.License);
    }

    [Fact]
    public void ContainsExactlyTheFourRequiredFiles()
    {
        ModelManifest manifest = ModelManifest.Bundled();
        var paths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "encoder.int4.onnx",
                "decoder.onnx",
                "joiner.onnx",
                "tokens.txt",
            },
            paths);
    }

    /// <summary>
    /// The manifest and the engine must ask for the same files. They are written down twice -
    /// once as what to download, once as what to refuse to load without - and a divergence
    /// would show up as a model that installs cleanly and then reports itself incomplete.
    /// </summary>
    [Fact]
    public void ManifestListsExactlyWhatTheEngineRequires()
    {
        ModelManifest manifest = ModelManifest.Bundled();

        Assert.Equal(
            SherpaOnnxAdapter.RequiredFiles(EncoderPrecision.Int4WeightOnly).ToHashSet(StringComparer.Ordinal),
            manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void DownloadSizeStaysWithinExpectedRange()
    {
        ModelManifest manifest = ModelManifest.Bundled();
        double megabytes = manifest.TotalByteCount / 1_000_000.0;

        // The set weighs about 720 MB. A sharp jump means a file leaked into it that recognition
        // does not need - the unquantized encoder alone weighs 2.4 GB.
        Assert.True(megabytes > 600, $"the set is suspiciously light: {megabytes} MB");
        Assert.True(megabytes < 800, $"the set is suspiciously heavy: {megabytes} MB");
    }

    [Fact]
    public void EveryFileHasDistinctPathAndReachableURL()
    {
        ModelManifest manifest = ModelManifest.Bundled();
        var paths = manifest.Files.Select(file => file.Path).ToList();

        Assert.Equal(paths.Count, paths.ToHashSet(StringComparer.Ordinal).Count);

        foreach (ModelManifest.File file in manifest.Files)
        {
            Uri url = Assert.IsType<Uri>(manifest.DownloadURL(file));
            Assert.Equal("https", url.Scheme);
            Assert.Equal("huggingface.co", url.Host);
        }
    }

    /// <summary>
    /// The acoustic term hinter has no ONNX publication, so asking for its manifest must say
    /// so in its own words - not hand out a Core ML file list that would install 98 MB this
    /// engine cannot read, and not look like a damaged model that a re-download would fix.
    /// </summary>
    [Fact]
    public void VocabularyManifestReportsThatTheEngineCannotUseIt()
    {
        ModelManifestError.EngineUnsupported error =
            Assert.Throws<ModelManifestError.EngineUnsupported>(ModelManifest.BundledVocabulary);

        Assert.Contains("Core ML", error.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parakeet Ultra at the revision, quantization and checksum upstream's desktop builds pin
    /// (<c>apps/desktop/src-tauri/model-manifest.json</c>): the same bytes, so the same text.
    /// </summary>
    [Fact]
    public void UltraManifestIsUpstreamsDesktopModel()
    {
        ModelManifest manifest = ModelManifest.BundledUltra();

        Assert.Equal("parakeet-ultra-gguf", manifest.ModelID);
        Assert.Equal("handy-computer/parakeet-ultra-gguf", manifest.Repository);
        Assert.Equal("a1539be1b849ea7463b88343e5277b373d0c4ea9", manifest.Revision);
        Assert.Equal("Q8_0", manifest.Quantization);
        Assert.Equal("CC-BY-4.0", manifest.License);
        ModelManifest.File file = Assert.Single(manifest.Files);
        Assert.Equal("parakeet-ultra-Q8_0.gguf", file.Path);
        Assert.Equal(740_363_168, file.ByteCount);
        Assert.Equal("007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59", file.Sha256);
        Assert.Equal(
            "https://huggingface.co/handy-computer/parakeet-ultra-gguf/resolve/a1539be1b849ea7463b88343e5277b373d0c4ea9/parakeet-ultra-Q8_0.gguf",
            manifest.DownloadURL(file)?.ToString());
    }

    /// <summary>The engine loads the one GGUF in the folder; the manifest must install exactly one.</summary>
    [Fact]
    public void UltraManifestListsExactlyOneModelFile()
    {
        ModelManifest manifest = ModelManifest.BundledUltra();

        Assert.Single(manifest.Files, file => file.Path.EndsWith(".gguf", StringComparison.Ordinal));
    }
}
