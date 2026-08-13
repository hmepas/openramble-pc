namespace DictationCore.Tests;

/// <summary>
/// Mock of the engine for pure logic tests.
///
/// The existence of this mock is the meaning of the protocol: not a single test
/// of DictationCore should pull in the recognition engine and load the model.
/// </summary>
/// <remarks>
/// Ported from a Swift <c>actor</c>: the state is guarded by a lock instead,
/// and every method returns an already-completed task.
/// </remarks>
internal sealed class MockASREngine : IASREngineAdapting
{
    private readonly object gate = new();
    private string? loadedDirectory;
    private int transcribeCallCount;
    private bool isLoaded;
    private ASRResult resultToReturn = new(
        text: "тест",
        audioDuration: TimeSpan.FromSeconds(1),
        processingDuration: TimeSpan.FromSeconds(0.01));

    private ASREngineError? errorToThrow;

    public string? LoadedDirectory
    {
        get { lock (gate) { return loadedDirectory; } }
    }

    public int TranscribeCallCount
    {
        get { lock (gate) { return transcribeCallCount; } }
    }

    public bool IsLoaded
    {
        get { lock (gate) { return isLoaded; } }
    }

    public Task LoadModels(string directory)
    {
        lock (gate)
        {
            if (errorToThrow is { } error) { return Task.FromException(error); }
            loadedDirectory = directory;
            isLoaded = true;
            return Task.CompletedTask;
        }
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        lock (gate)
        {
            transcribeCallCount += 1;
            if (errorToThrow is { } error) { return Task.FromException<ASRResult>(error); }
            if (!isLoaded) { return Task.FromException<ASRResult>(new ASREngineError.ModelsNotLoaded()); }
            return Task.FromResult(resultToReturn);
        }
    }

    public Task Unload()
    {
        lock (gate)
        {
            isLoaded = false;
            return Task.CompletedTask;
        }
    }

    public void SetError(ASREngineError? error)
    {
        lock (gate) { errorToThrow = error; }
    }

    public void SetResult(ASRResult result)
    {
        lock (gate) { resultToReturn = result; }
    }
}

public sealed class ASREngineAdaptingTests
{
    [Fact]
    public async Task TestTranscribeBeforeLoadingFails()
    {
        var engine = new MockASREngine();

        await Assert.ThrowsAsync<ASREngineError.ModelsNotLoaded>(
            () => engine.Transcribe([0, 0, 0]));
    }

    [Fact]
    public async Task TestUnloadMakesEngineUnusableAgain()
    {
        var engine = new MockASREngine();
        const string directory = "/tmp/models";

        await engine.LoadModels(directory);
        var result = await engine.Transcribe([0.1f, 0.2f]);
        Assert.Equal("тест", result.Text);

        await engine.Unload();

        await Assert.ThrowsAsync<ASREngineError.ModelsNotLoaded>(
            () => engine.Transcribe([0.1f]));
    }

    [Fact]
    public void TestResultCarriesWordTimings()
    {
        var result = new ASRResult(
            text: "привет мир",
            words:
            [
                new ASRResult.Word(
                    Text: "привет",
                    Start: TimeSpan.Zero,
                    End: TimeSpan.FromSeconds(0.4)),
                new ASRResult.Word(
                    Text: "мир",
                    Start: TimeSpan.FromSeconds(0.5),
                    End: TimeSpan.FromSeconds(0.8),
                    Confidence: 0.9),
            ],
            audioDuration: TimeSpan.FromSeconds(1.0),
            processingDuration: TimeSpan.FromSeconds(0.05));

        Assert.Equal(2, result.Words.Count);
        Assert.Equal(0.9, result.Words[1].Confidence);
        // Timings are monotonous: the end of a word is not earlier than its beginning, words do not overlap.
        Assert.True(result.Words[0].End <= result.Words[1].Start);
    }
}
