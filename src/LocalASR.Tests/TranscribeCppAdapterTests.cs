using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// The transcribe.cpp adapter: its pure parts without the runtime, its error paths against the
/// runtime when <c>scripts/build-transcribe.sh</c> has built it, and one recognition when
/// Parakeet Ultra is on disk.
/// </summary>
/// <remarks>
/// The word tests are ported from <c>recognized()</c>'s tests in upstream's
/// <c>core/ramble-engine</c>; the model-file tests from the Swift adapter's
/// <c>modelFile(in:)</c> contract.
/// </remarks>
public sealed class TranscribeCppAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tcpp-{Guid.NewGuid()}");

    public TranscribeCppAdapterTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void WordTimesAreClampedIntoTheClipAndNeverEndBeforeTheyStart()
    {
        IReadOnlyList<ASRResult.Word> words = TranscribeCppAdapter.Words(
            [("early", -40, 300), ("late", 900, 1_400), ("inverted", 700, 650)],
            clipSeconds: 1.0);

        Assert.Equal(
            [
                new ASRResult.Word("early", TimeSpan.Zero, TimeSpan.FromMilliseconds(300)),
                new ASRResult.Word("late", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1_000)),
                new ASRResult.Word("inverted", TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(700)),
            ],
            words);
    }

    [Fact]
    public void EmptyWordsAreDroppedAndTheOrderIsKept()
    {
        IReadOnlyList<ASRResult.Word> words = TranscribeCppAdapter.Words(
            [(" one", 0, 100), ("  ", 100, 120), ("", 120, 130), (null, 130, 140), ("two ", 130, 300)],
            clipSeconds: 1.0);

        Assert.Equal(["one", "two"], words.Select(word => word.Text));
    }

    [Fact]
    public void TheModelIsTheOneGgufInTheFolder()
    {
        string model = Path.Combine(root, "parakeet-ultra-Q8_0.gguf");
        File.WriteAllText(model, "x");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x");

        Assert.Equal(model, TranscribeCppAdapter.ModelFile(root));
    }

    [Fact]
    public void AFolderWithoutAModelOrWithTwoIsRefusedLoudly()
    {
        ASREngineError.ModelsUnavailable none =
            Assert.Throws<ASREngineError.ModelsUnavailable>(() => TranscribeCppAdapter.ModelFile(root));
        Assert.Contains("no model file", none.Reason, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(root, "a.gguf"), "x");
        File.WriteAllText(Path.Combine(root, "b.GGUF"), "x");
        ASREngineError.ModelsUnavailable two =
            Assert.Throws<ASREngineError.ModelsUnavailable>(() => TranscribeCppAdapter.ModelFile(root));
        Assert.Contains("2 model files", two.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// One thread would put the whole encoder on one core (a 6 s take 0.66 s at one thread
    /// against 0.18 s at four), and the encoder keeps scaling past four (a 45 s clip 1.34 s at
    /// four, 0.82 s at eight), so the default is half the logical processors, within [1, 8].
    /// </summary>
    [Fact]
    public void ShipsHalfTheLogicalProcessorsWithinOneToEightThreads()
    {
        Assert.Equal(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), new TranscribeCppAdapter().Threads);
        Assert.InRange(TranscribeCppAdapter.DefaultThreadCount, 1, 8);
        Assert.Equal(1, new TranscribeCppAdapter(threadCount: 0).Threads);
    }

    [Fact]
    public async Task TaughtWordsAreTrimmedAndBlanksNeverReachTheRuntime()
    {
        var adapter = new TranscribeCppAdapter();

        await adapter.SetVocabulary([" Kubernetes ", "", "  ", "OpenRamble"]);

        Assert.Equal(["Kubernetes", "OpenRamble"], adapter.Vocabulary);
    }

    [Fact]
    public async Task TranscribingWithoutAModelIsReportedRatherThanGuessed()
    {
        var adapter = new TranscribeCppAdapter();

        await Assert.ThrowsAsync<ASREngineError.ModelsNotLoaded>(() => adapter.Transcribe([0.1f]));
    }

    [Fact]
    public async Task UnloadWithoutLoadIsHarmless()
    {
        var adapter = new TranscribeCppAdapter();

        await adapter.Unload();

        Assert.False(adapter.IsLoaded);
    }

    /// <summary>
    /// The binding's structs are checked against the library's own sizes before any is handed
    /// over; garbage in a file is a load error, not a crash.
    /// </summary>
    [NativeRuntimeFact]
    public async Task AFileThatIsNotAModelIsALoadErrorAndTheRuntimeAgreesOnItsStructs()
    {
        File.WriteAllText(Path.Combine(root, "broken.gguf"), "not really a model");
        var adapter = new TranscribeCppAdapter();

        Assert.StartsWith("0.4.1", TranscribeCppAdapter.RuntimeVersion, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ASREngineError.ModelsUnavailable>(() => adapter.LoadModels(root));
        Assert.False(adapter.IsLoaded);
    }

    /// <summary>
    /// The whole chain on real weights: WAV off the disk, through the engine, into text and word
    /// timings - and the taught words passed on every run without disturbing it.
    /// </summary>
    [InstalledUltraFact]
    public async Task TranscribesTheFixtureWithParakeetUltra()
    {
        var adapter = new TranscribeCppAdapter();
        var transcriber = new LocalTranscriber(adapter);
        await transcriber.Prepare(InstalledUltraFactAttribute.Directory!);
        try
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "en.wav");
            ASRResult result = await transcriber.Transcribe(fixture);

            string[] heard = result.Text
                .ToLowerInvariant()
                .Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("country", heard);
            Assert.Contains("ask", heard);
            Assert.NotEmpty(result.Words);
            Assert.All(result.Words, word => Assert.InRange(word.End, word.Start, result.AudioDuration));

            await transcriber.SetVocabulary(["Kennedy"]);
            ASRResult taught = await transcriber.Transcribe(fixture);
            Assert.Contains("country", taught.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await transcriber.Unload();
        }
    }
}

/// <summary>A fact that needs the transcribe.cpp runtime next to the test assembly.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeRuntimeFactAttribute : FactAttribute
{
    public NativeRuntimeFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(Path.Combine(AppContext.BaseDirectory, "libtranscribe.so")))
        {
            Skip = "The transcribe.cpp runtime is not built. Run scripts/build-transcribe.sh.";
        }
    }
}

/// <summary>A fact that needs Parakeet Ultra on disk and the runtime to run it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InstalledUltraFactAttribute : FactAttribute
{
    public InstalledUltraFactAttribute()
    {
        if (Directory is null || !File.Exists(Path.Combine(AppContext.BaseDirectory, "libtranscribe.so")))
        {
            Skip = "Parakeet Ultra is not installed. Set OPENRAMBLE_ULTRA_MODEL_DIR to the folder "
                + "holding its GGUF (asr-bench install puts one there) and run scripts/build-transcribe.sh.";
        }
    }

    public static string? Directory
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("OPENRAMBLE_ULTRA_MODEL_DIR");
            return string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path) ? null : path;
        }
    }
}
