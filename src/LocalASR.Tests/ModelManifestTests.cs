using System.Text;

namespace LocalASR.Tests;

public class ModelManifestTests
{
    private const string ValidSHA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ValidRevision = "aed02740059203c4a87495924f685de3722ae9ce";

    private static byte[] ManifestJSON(string? revision = null, string? files = null)
    {
        string defaultFiles =
            $$"""[{"path":"Encoder.mlmodelc/weights/weight.bin","byteCount":100,"sha256":"{{ValidSHA}}"}]""";
        return Encoding.UTF8.GetBytes($$"""
        {
          "modelID": "parakeet-tdt-0.6b-v3",
          "repository": "FluidInference/parakeet-tdt-0.6b-v3-coreml",
          "revision": "{{revision ?? ValidRevision}}",
          "fluidAudioVersion": "0.15.5",
          "quantization": "encoder 6-bit palettized, mixed precision",
          "license": "CC-BY-4.0",
          "files": {{files ?? defaultFiles}}
        }
        """);
    }

    [Fact]
    public void DecodesValidManifest()
    {
        ModelManifest manifest = ModelManifest.Decode(ManifestJSON());

        Assert.Equal(ValidRevision, manifest.Revision);
        Assert.Equal("CC-BY-4.0", manifest.License);
        Assert.Equal(100, manifest.TotalByteCount);
    }

    [Fact]
    public void DownloadURLPinsRevision()
    {
        ModelManifest manifest = ModelManifest.Decode(ManifestJSON());
        Uri url = Assert.IsType<Uri>(manifest.DownloadURL(manifest.Files[0]));

        // The link must contain a specific revision, not main: otherwise the content may
        // change under us and the checksums will no longer converge.
        Assert.Contains($"/resolve/{ValidRevision}/", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("/main/", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsBranchNameAsRevision() => AssertInvalid(ManifestJSON(revision: "main"));

    [Fact]
    public void RejectsShortRevision() => AssertInvalid(ManifestJSON(revision: "aed0274"));

    [Fact]
    public void RejectsPathTraversal() =>
        AssertInvalid(ManifestJSON(
            files: $$"""[{"path":"../../etc/passwd","byteCount":10,"sha256":"{{ValidSHA}}"}]"""));

    [Fact]
    public void RejectsAbsolutePath() =>
        AssertInvalid(ManifestJSON(
            files: $$"""[{"path":"/etc/passwd","byteCount":10,"sha256":"{{ValidSHA}}"}]"""));

    [Fact]
    public void RejectsUppercaseChecksum() =>
        AssertInvalid(ManifestJSON(
            files: $$"""[{"path":"a.bin","byteCount":10,"sha256":"{{ValidSHA.ToUpperInvariant()}}"}]"""));

    [Fact]
    public void RejectsZeroByteFile() =>
        AssertInvalid(ManifestJSON(
            files: $$"""[{"path":"a.bin","byteCount":0,"sha256":"{{ValidSHA}}"}]"""));

    [Fact]
    public void RejectsEmptyFileList() => AssertInvalid(ManifestJSON(files: "[]"));

    private static void AssertInvalid(byte[] data)
    {
        ModelManifestError error = Assert.Throws<ModelManifestError.Invalid>(
            () => ModelManifest.Decode(data));
        Assert.IsNotType<ModelManifestError.Malformed>(error);
    }
}

public class ModelDownloadPolicyTests
{
    private readonly ModelDownloadPolicy policy = new();

    [Fact]
    public void AllowsPinnedModelAndApprovedRedirectHosts()
    {
        string[] allowed =
        [
            "https://huggingface.co/org/model/file",
            "https://cdn-lfs.hf.co/file",
            "https://us-east-1.aws.huggingface.co/file",
            "https://github.com/org/repo/releases/download/v1/file",
            "https://objects.githubusercontent.com/file",
            "https://release-assets.githubusercontent.com/file",
        ];

        foreach (string raw in allowed)
        {
            policy.Validate(new Uri(raw));
        }
    }

    [Fact]
    public void RejectsHTTPAndLookalikeHosts()
    {
        string[] rejected =
        [
            "http://huggingface.co/file",
            "https://huggingface.co.attacker.example/file",
            "https://githubusercontent.com.attacker.example/file",
            "https://pages.github.com/file",
            "https://example.com/file",
        ];

        foreach (string raw in rejected)
        {
            Assert.Throws<ModelDownloadError.UnapprovedURL>(() => policy.Validate(new Uri(raw)));
        }
    }

    // MARK: - Compiled hint manifest

    /// <summary>
    /// The hinter's manifest was the second root of trust. On this port there is nothing for
    /// it to be the root of: no ONNX export of Parakeet CTC 110M has been published, so the
    /// only thing it could describe is Core ML bundles the engine cannot open. Asking for it
    /// must say that in its own words - a download of 98 MB that nothing can read is worse
    /// than a refusal, and so is a failure shaped like a damaged model.
    /// </summary>
    [Fact]
    public void Scenario001()
    {
        ModelManifestError.EngineUnsupported error =
            Assert.Throws<ModelManifestError.EngineUnsupported>(ModelManifest.BundledVocabulary);

        Assert.Contains("Parakeet CTC 110M", error.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compiled resource itself is kept, unchanged and still valid, so that the day an
    /// ONNX export appears this is a data change and not a code change. The test reads it the
    /// long way round on purpose: the short way is exactly the door that is now closed.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        using Stream resource = Assert.IsAssignableFrom<Stream>(
            typeof(ModelManifest).Assembly.GetManifestResourceStream(
                "LocalASR.Resources.vocabulary-manifest.json"));
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);

        ModelManifest vocabulary = ModelManifest.Decode(buffer.ToArray());
        ModelManifest main = ModelManifest.Bundled();

        // Both models live in different installation folders: they have their own
        // repositories, revisions and life cycles.
        Assert.Equal("parakeet-ctc-110m", vocabulary.ModelID);
        Assert.NotEqual(main.ModelID, vocabulary.ModelID);
        Assert.NotEqual(main.Revision, vocabulary.Revision);
    }
}
