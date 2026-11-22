using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// The engine follows the files: a GGUF folder goes to transcribe.cpp, an ONNX folder to
/// sherpa-onnx, and only one of them holds weights at a time.
/// </summary>
public sealed class EngineByModelFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"engine-by-files-{Guid.NewGuid()}");

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string Folder(string name, string file)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), "x");
        return folder;
    }

    [Fact]
    public async Task EachFolderGoesToTheEngineThatReadsItsFiles()
    {
        var gguf = new RecordingEngine("gguf");
        var onnx = new RecordingEngine("onnx");
        var engine = new EngineByModelFiles(gguf, onnx);

        await engine.LoadModels(Folder("v3", "encoder.int4.onnx"));
        Assert.Equal("onnx", (await engine.Transcribe([0f])).Text);

        await engine.LoadModels(Folder("ultra", "parakeet-ultra-Q8_0.gguf"));
        Assert.Equal("gguf", (await engine.Transcribe([0f])).Text);

        Assert.Equal(1, onnx.Loads);
        Assert.Equal(1, gguf.Loads);
    }

    /// <summary>The extension is matched as the adapter matches it, whatever its case.</summary>
    [Fact]
    public async Task AnUppercaseGgufGoesToTranscribeCpp()
    {
        var gguf = new RecordingEngine("gguf");
        var engine = new EngineByModelFiles(gguf, new RecordingEngine("onnx"));

        await engine.LoadModels(Folder("ultra-upper", "PARAKEET-ULTRA.GGUF"));

        Assert.Equal("gguf", (await engine.Transcribe([0f])).Text);
    }

    [Fact]
    public async Task SwitchingModelsGivesBackTheOtherEnginesWeightsFirst()
    {
        var gguf = new RecordingEngine("gguf");
        var onnx = new RecordingEngine("onnx");
        var engine = new EngineByModelFiles(gguf, onnx);

        await engine.LoadModels(Folder("v3", "tokens.txt"));
        await engine.LoadModels(Folder("ultra", "model.gguf"));

        Assert.Equal(1, onnx.Unloads);
        Assert.True(onnx.LastUnload < gguf.LastLoad, "the old weights go before the new ones arrive");
    }

    [Fact]
    public async Task NothingLoadedIsReportedRatherThanGuessed()
    {
        var engine = new EngineByModelFiles(new RecordingEngine("gguf"), new RecordingEngine("onnx"));

        await Assert.ThrowsAsync<ASREngineError.ModelsNotLoaded>(() => engine.Transcribe([0f]));
    }

    [Fact]
    public async Task TaughtWordsReachBothEngines()
    {
        var gguf = new RecordingEngine("gguf");
        var onnx = new RecordingEngine("onnx");
        var engine = new EngineByModelFiles(gguf, onnx);

        await engine.SetVocabulary(["OpenRamble"]);

        Assert.Equal(["OpenRamble"], gguf.Vocabulary);
        Assert.Equal(["OpenRamble"], onnx.Vocabulary);
    }

    private sealed class RecordingEngine(string text) : IASREngineAdapting
    {
        private static long clock;

        public int Loads { get; private set; }

        public int Unloads { get; private set; }

        public long LastLoad { get; private set; }

        public long LastUnload { get; private set; }

        public IReadOnlyList<string> Vocabulary { get; private set; } = [];

        public Task LoadModels(string directory)
        {
            Loads++;
            LastLoad = Interlocked.Increment(ref clock);
            return Task.CompletedTask;
        }

        public Task<ASRResult> Transcribe(float[] samples) =>
            Task.FromResult(new ASRResult(text, TimeSpan.Zero, TimeSpan.Zero));

        public Task Unload()
        {
            Unloads++;
            LastUnload = Interlocked.Increment(ref clock);
            return Task.CompletedTask;
        }

        public Task SetVocabulary(IReadOnlyList<string> terms)
        {
            Vocabulary = terms;
            return Task.CompletedTask;
        }
    }
}
