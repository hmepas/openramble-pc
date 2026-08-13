namespace DictationCore.Tests;

/// <summary>The origin of the dictation reaches the application - and only when inserted.</summary>
[Collection(DictationControllerCollection.Name)]
public sealed class DictationProvenanceTests
{
    private readonly FakeCapture capture = new();
    private readonly FakeInserter inserter = new();
    private readonly FakeOverlay overlay = new();
    private readonly FakeSounds sounds = new();

    private DictationController MakeController(
        string recognized,
        IReadOnlyList<DictionaryReplacement>? replacements = null) =>
        new(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                recognized,
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            pipeline: () => new TextPipeline(replacements: replacements ?? []));

    private static async Task Settle(int iterations = 12)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TestProvenanceIsReportedForEverySuccessfulInsertion()
    {
        var controller = MakeController(
            recognized: "открой поуст герз",
            replacements: [new DictionaryReplacement(spoken: "поуст герз", written: "Postgres", inflects: false)]);
        PipelineProvenance? reported = null;
        controller.OnDictationCompleted = provenance => reported = provenance;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.NotNull(reported);
        // Verbatim is what the person said.
        Assert.Equal("открой поуст герз", reported.Raw);
        Assert.Equal("открой Postgres", reported.AfterDictionary);
        Assert.Equal("Открой Postgres", reported.FinalText);

        // Exactly what was recorded as final is what got inserted.
        Assert.Equal([reported.FinalText], inserter.InsertedTexts);
    }

    /// <summary>
    /// Nothing was inserted - there is no origin.
    ///
    /// Otherwise "copy verbatim" would hand out a text the person never saw,
    /// while they are waiting for the last inserted dictation.
    /// </summary>
    [Fact]
    public async Task TestProvenanceIsNotReportedWhenNothingWasInserted()
    {
        var controller = MakeController(recognized: "   ");
        PipelineProvenance? reported = null;
        controller.OnDictationCompleted = provenance => reported = provenance;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Null(reported);
    }

    [Fact]
    public async Task TestProvenanceIsNotReportedWhenInsertionFails()
    {
        var controller = MakeController(recognized: "привет мир");
        inserter.SetError(new TextInsertionError.SecureInputActive());
        PipelineProvenance? reported = null;
        controller.OnDictationCompleted = provenance => reported = provenance;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        // A failed insertion gives no origin.
        Assert.Null(reported);
    }
}
