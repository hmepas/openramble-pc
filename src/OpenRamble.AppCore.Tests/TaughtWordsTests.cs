using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The words a person taught the dictionary reach the recognizer, which leans toward those
/// spellings while it decodes.
/// </summary>
/// <remarks>
/// Ported from upstream's <c>TaughtWordsTests.swift</c>. Upstream hands the list to two
/// recognizers, dictation and file import; this port has one, the shared transcriber.
/// </remarks>
public sealed class TaughtWordsTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public void OnlyTaughtWordsThatMayBoostReachTheDecoderOnce()
    {
        IReadOnlyList<string> terms = AppState.VocabularyTerms(
        [
            new DictionaryReplacement("кубернетис", "Kubernetes"),
            new DictionaryReplacement("кубер", "Kubernetes"),
            new DictionaryReplacement("коммит", "commit", noAcousticBoost: true),
            new DictionaryReplacement("опен рэмбл", "OpenRamble"),
        ]);

        Assert.Equal(["Kubernetes", "OpenRamble"], terms);
    }

    [Fact]
    public async Task ANewWordReachesTheRecognizerAndARemovedOneLeaves()
    {
        var engine = new VocabularyRecordingEngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the launch list", () => engine.Vocabularies.Count > 0);
        Assert.Empty(engine.Vocabularies[^1]);

        state.AddReplacement("open ramble", "OpenRamble");
        await TestWait.For("the taught word", () => engine.Vocabularies[^1].SequenceEqual(["OpenRamble"]));

        state.RemoveReplacements([0]);
        await TestWait.For("the word gone", () => engine.Vocabularies[^1].Count == 0);
    }

    [Fact]
    public async Task WordsTaughtBeforeThisLaunchAreHandedOverAtLaunch()
    {
        AppState first = harness.MakeState();
        first.AddReplacement("tori", "Tauri");
        Assert.Equal(["Tauri"], first.Replacements.Select(replacement => replacement.Written));

        var engine = new VocabularyRecordingEngine();
        harness.WarmUpEngine = engine;
        _ = harness.MakeState();
        await TestWait.For("the stored word", () => engine.Vocabularies.Any(list => list.SequenceEqual(["Tauri"])));
    }

    /// <summary>An engine that loads anything and keeps every list of taught words it was given.</summary>
    private sealed class VocabularyRecordingEngine : IASREngineAdapting
    {
        private readonly Lock gate = new();
        private readonly List<IReadOnlyList<string>> vocabularies = [];

        public IReadOnlyList<IReadOnlyList<string>> Vocabularies { get { lock (gate) { return [.. vocabularies]; } } }

        public Task LoadModels(string directory) => Task.CompletedTask;

        public Task<ASRResult> Transcribe(float[] samples) =>
            Task.FromResult(new ASRResult(string.Empty, TimeSpan.FromSeconds(1), TimeSpan.Zero));

        public Task Unload() => Task.CompletedTask;

        public Task SetVocabulary(IReadOnlyList<string> terms)
        {
            lock (gate) { vocabularies.Add(terms); }
            return Task.CompletedTask;
        }
    }
}
