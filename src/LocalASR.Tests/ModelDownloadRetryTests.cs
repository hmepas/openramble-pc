using System.Net.Sockets;
using System.Text;

namespace LocalASR.Tests;

/// <summary>
/// Retrying a flaky transfer.
///
/// The install is 586 MB across many files, and the encoder alone is 92% of it. A single
/// dropped TLS handshake anywhere in that stream used to fail the whole installation and
/// leave onboarding on "Model installation failed" - the user had done nothing wrong and had
/// nothing to fix. Reported from a real machine behind a VPN, where both mirrors blinked
/// within the same attempt.
///
/// The rule that matters most here is what is NOT retried. A retry that hammers a disallowed
/// host, ignores a cancel, or re-downloads half a gigabyte against a 404 is worse than the
/// original failure.
/// </summary>
public class ModelDownloadRetryTests : IDisposable
{
    private readonly string root;

    private static readonly byte[] FileA = Encoding.UTF8.GetBytes("first file");
    private static readonly byte[] FileB = Encoding.UTF8.GetBytes("second file");

    public ModelDownloadRetryTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"retry-{Guid.NewGuid():D}");
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

    private static Dictionary<string, byte[]> Contents() => new()
    {
        ["Encoder.mlmodelc/weight.bin"] = FileA,
        ["vocab.json"] = FileB,
    };

    private static ModelManifest MakeManifest() => new(
        modelID: "test-model",
        repository: "acme/test",
        revision: new string('a', 40),
        fluidAudioVersion: "0.15.5",
        quantization: "test",
        license: "CC-BY-4.0",
        files:
        [
            new ModelManifest.File("Encoder.mlmodelc/weight.bin", FileA.Length, ModelStoreTests.Sha256(FileA)),
            new ModelManifest.File("vocab.json", FileB.Length, ModelStoreTests.Sha256(FileB)),
        ],
        mirrorSource: new ModelManifest.Mirror("mikwiseman/openramble", "models-test"));

    /// <summary>
    /// Retries are instant in tests. The production delay is real, and waiting it out here
    /// would trade seconds of test time for nothing.
    /// </summary>
    private ModelStore MakeStore(IModelDownloading downloader)
    {
        ModelManifest manifest = MakeManifest();
        return new ModelStore(
            manifest,
            new ModelInstallLayout(
                root: root,
                modelID: manifest.ModelID,
                revision: manifest.Revision,
                engineFolderName: "repo"),
            downloader,
            retryDelay: TimeSpan.Zero);
    }

    // MARK: - What is retried

    /// <summary>The reported failure: the primary blinks, then works.</summary>
    [Fact]
    public async Task TransientNetworkFailureIsRetriedOnTheSameSource()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetTransientFailures(1, "huggingface.co");

        ModelStore store = MakeStore(downloader);
        await store.Install();

        Assert.True(store.CurrentState().IsReady, $"expected a finished install, got {store.CurrentState()}");
        Assert.DoesNotContain("github.com", downloader.RequestedHosts);
    }

    /// <summary>
    /// The exact pair of errors from the screenshot: both mirrors blink inside one attempt.
    /// Before the retry this was a dead end.
    /// </summary>
    [Fact]
    public async Task BothMirrorsBlinkingStillInstalls()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetTransientFailures(
            1, "huggingface.co", new ModelDownloadError.Network("Could not connect to the server."));
        downloader.SetTransientFailures(
            1, "github.com", new ModelDownloadError.Network("A TLS error caused the secure connection to fail."));

        ModelStore store = MakeStore(downloader);
        await store.Install();

        Assert.True(store.CurrentState().IsReady, $"expected a finished install, got {store.CurrentState()}");
    }

    /// <summary>A server-side 5xx is the server's bad minute, not our bad address.</summary>
    [Fact]
    public async Task ServerErrorIsRetried()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetTransientFailures(1, "huggingface.co", new ModelDownloadError.HttpStatus(503));

        ModelStore store = MakeStore(downloader);
        await store.Install();

        Assert.True(store.CurrentState().IsReady, $"expected a finished install, got {store.CurrentState()}");
    }

    /// <summary>
    /// Retrying is bounded. An endlessly flaky source must hand over to the mirror rather than
    /// spin on half a gigabyte forever.
    /// </summary>
    [Fact]
    public async Task RetriesAreBoundedAndThenTheMirrorIsUsed()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetFailure(new ModelDownloadError.Network("down"), "huggingface.co");

        await MakeStore(downloader).Install();

        IReadOnlyDictionary<string, int> attempts = downloader.AttemptsByHost;
        Assert.True(attempts.ContainsKey("github.com"), "the mirror must be reached");
        int primary = attempts.GetValueOrDefault("huggingface.co");
        Assert.True(primary > 2, "the primary deserves more than one try");
        Assert.True(primary <= 8, "but not an unbounded number of them");
    }

    // MARK: - What is never retried

    /// <summary>
    /// Cancel means cancel. Retrying after it would keep pulling 586 MB against a direct
    /// command.
    /// </summary>
    [Fact]
    public async Task CancellationIsNeverRetried()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetFailure(new ModelDownloadError.Cancelled());

        ModelStore store = MakeStore(downloader);
        await store.Install();

        Assert.Equal(new ModelState.NotInstalled(), store.CurrentState());
        Assert.Equal(1, downloader.AttemptsByHost.Values.Sum());
    }

    /// <summary>
    /// A redirect off the allowlist is a policy violation, not a bad minute. Retrying it would
    /// knock on a disallowed host repeatedly.
    /// </summary>
    [Fact]
    public async Task DisallowedHostIsNeverRetried()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetFailure(new ModelDownloadError.UnapprovedURL("evil.example.com"), "huggingface.co");

        await MakeStore(downloader).Install();

        Assert.Equal(1, downloader.AttemptsByHost["huggingface.co"]);
    }

    /// <summary>A 404 is a wrong address. No number of retries turns it into a right one.</summary>
    [Fact]
    public async Task NotFoundIsNeverRetried()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetFailure(new ModelDownloadError.HttpStatus(404), "huggingface.co");

        await MakeStore(downloader).Install();

        Assert.Equal(1, downloader.AttemptsByHost["huggingface.co"]);
    }

    /// <summary>
    /// A size mismatch means the source holds a different file. Re-pulling half a gigabyte
    /// cannot change what is on the other end.
    /// </summary>
    [Fact]
    public async Task SizeMismatchIsNeverRetried()
    {
        var downloader = new FakeDownloader(Contents());
        downloader.SetFailure(new ModelDownloadError.UnexpectedSize(10, 20), "huggingface.co");

        await MakeStore(downloader).Install();

        Assert.Equal(1, downloader.AttemptsByHost["huggingface.co"]);
    }

    /// <summary>Both sources genuinely gone: the message still names both, as before.</summary>
    [Fact]
    public async Task PermanentFailureOfBothSourcesNamesBoth()
    {
        var downloader = new FakeDownloader([]);
        downloader.SetFailure(new ModelDownloadError.HttpStatus(404));

        ModelStore store = MakeStore(downloader);
        await store.Install();

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        var download = Assert.IsType<ModelStoreError.Download>(failed.Error);
        Assert.Contains("huggingface.co", download.Reason, StringComparison.Ordinal);
        Assert.Contains("github.com", download.Reason, StringComparison.Ordinal);
    }
}

/// <summary>A full disk is this machine's problem, and retrying makes it worse.</summary>
public class LocalWriteFailureTests : IDisposable
{
    private readonly string root;
    private static readonly byte[] FileA = Encoding.UTF8.GetBytes("first file");
    private static readonly byte[] FileB = Encoding.UTF8.GetBytes("second file");

    public LocalWriteFailureTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"disk-{Guid.NewGuid():D}");
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

    private ModelStore MakeStore(IModelDownloading downloader)
    {
        var manifest = new ModelManifest(
            modelID: "test-model",
            repository: "acme/test",
            revision: new string('a', 40),
            fluidAudioVersion: "0.15.5",
            quantization: "test",
            license: "CC-BY-4.0",
            files:
            [
                new ModelManifest.File("Encoder.mlmodelc/weight.bin", FileA.Length, ModelStoreTests.Sha256(FileA)),
                new ModelManifest.File("vocab.json", FileB.Length, ModelStoreTests.Sha256(FileB)),
            ],
            mirrorSource: new ModelManifest.Mirror("mikwiseman/openramble", "models-test"));
        return new ModelStore(
            manifest,
            new ModelInstallLayout(
                root: root,
                modelID: manifest.ModelID,
                revision: manifest.Revision,
                engineFolderName: "repo"),
            downloader,
            retryDelay: TimeSpan.Zero);
    }

    /// <summary>
    /// One attempt, not six. Before this, a disk that filled partway through the 425 MB
    /// encoder pulled it six times - roughly 2.7 GB - and still failed.
    /// </summary>
    [Fact]
    public async Task FullDiskIsNotRetried()
    {
        var downloader = new FakeDownloader([]);
        downloader.SetFailure(new ModelDownloadError.LocalWriteFailed(
            "The file couldn't be saved because there isn't enough space."));

        await MakeStore(downloader).Install();

        Assert.Equal(
            1,
            downloader.AttemptsByHost.Values.Sum());
    }

    /// <summary>The reason reaches the user instead of being reported as a network fault.</summary>
    [Fact]
    public async Task FullDiskSaysSoInsteadOfBlamingTheNetwork()
    {
        var downloader = new FakeDownloader([]);
        downloader.SetFailure(new ModelDownloadError.LocalWriteFailed("there isn't enough space"));

        ModelStore store = MakeStore(downloader);
        await store.Install();

        var failed = Assert.IsType<ModelState.Failed>(store.CurrentState());
        var download = Assert.IsType<ModelStoreError.Download>(failed.Error);
        Assert.Contains("enough space", download.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The classifier must not mistake a dropped connection for a full disk - that would stop
    /// retrying the case retries exist for.
    /// </summary>
    /// <remarks>
    /// Swift feeds it NSError domains; the C# downloader classifies exceptions instead, so
    /// the fixtures are the exceptions .NET actually raises in those two situations.
    /// </remarks>
    [Fact]
    public void NetworkErrorsAreNotMistakenForDiskErrors()
    {
        Assert.True(HttpClientModelDownloader.IsLocalWriteFailure(
            new IOException("There is not enough space on the disk.")));
        Assert.True(HttpClientModelDownloader.IsLocalWriteFailure(
            new UnauthorizedAccessException("Access to the path is denied.")));
        Assert.True(HttpClientModelDownloader.IsLocalWriteFailure(
            new DirectoryNotFoundException("Could not find a part of the path.")));
        Assert.False(HttpClientModelDownloader.IsLocalWriteFailure(
            new HttpRequestException("The connection was closed.")));
        Assert.False(HttpClientModelDownloader.IsLocalWriteFailure(
            new IOException("The response ended prematurely.", new SocketException(104))));
        Assert.False(HttpClientModelDownloader.IsLocalWriteFailure(
            new TaskCanceledException("The request timed out.")));
    }
}

/// <summary>Corrupt content is what the mirror is for.</summary>
public class CorruptContentFallbackTests : IDisposable
{
    private readonly string root;
    private static readonly byte[] Good = Encoding.UTF8.GetBytes("the real bytes");
    private static readonly byte[] Other = Encoding.UTF8.GetBytes("wrong contents");

    public CorruptContentFallbackTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"corrupt-{Guid.NewGuid():D}");
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

    private ModelStore MakeStore(IModelDownloading downloader)
    {
        var manifest = new ModelManifest(
            modelID: "test-model",
            repository: "acme/test",
            revision: new string('a', 40),
            fluidAudioVersion: "0.15.5",
            quantization: "test",
            license: "CC-BY-4.0",
            files: [new ModelManifest.File("vocab.json", Good.Length, ModelStoreTests.Sha256(Good))],
            mirrorSource: new ModelManifest.Mirror("mikwiseman/openramble", "models-test"));
        return new ModelStore(
            manifest,
            new ModelInstallLayout(
                root: root,
                modelID: manifest.ModelID,
                revision: manifest.Revision,
                engineFolderName: "repo"),
            downloader,
            retryDelay: TimeSpan.Zero);
    }

    /// <summary>
    /// The primary serves the right number of wrong bytes; the mirror is clean.
    ///
    /// Before this, verification ran only as a final pass, so the install died with a
    /// checksum error, deleted everything, and sent the user back to the same bad source on
    /// retry. The manifest promises the mirror exists so a single source is not a single
    /// point of failure.
    /// </summary>
    [Fact]
    public async Task CorruptPrimaryFallsBackToTheMirror()
    {
        // Same length, different bytes: the size check cannot catch this.
        Assert.Equal(Good.Length, Other.Length);

        var downloader = new FakeDownloader(new Dictionary<string, byte[]> { ["vocab.json"] = Good });
        downloader.SetContents(new Dictionary<string, byte[]> { ["vocab.json"] = Other }, "huggingface.co");

        ModelStore store = MakeStore(downloader);
        await store.Install();

        Assert.True(store.CurrentState().IsReady, $"the mirror should have saved it, got {store.CurrentState()}");
        Assert.Contains("github.com", downloader.RequestedHosts);
    }

    /// <summary>Corrupt on both sides still fails, and says why.</summary>
    [Fact]
    public async Task CorruptOnBothSourcesFailsClearly()
    {
        var downloader = new FakeDownloader(new Dictionary<string, byte[]> { ["vocab.json"] = Other });

        ModelStore store = MakeStore(downloader);
        await store.Install();

        Assert.IsType<ModelState.Failed>(store.CurrentState());
    }

    /// <summary>Re-serving the same corrupt bytes is pointless, so it is not retried.</summary>
    [Fact]
    public async Task CorruptContentIsNotRetriedOnTheSameSource()
    {
        var downloader = new FakeDownloader(new Dictionary<string, byte[]> { ["vocab.json"] = Other });

        await MakeStore(downloader).Install();

        IReadOnlyDictionary<string, int> attempts = downloader.AttemptsByHost;
        Assert.Equal(1, attempts["huggingface.co"]);
        Assert.Equal(1, attempts["github.com"]);
    }
}
