using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// Checks the gluing of tokens into words, the file-set gate, and the engine knobs.
///
/// No model is needed for any of it except the one integration test at the bottom:
/// <see cref="SherpaOnnxAdapter.Words"/> is a pure function, and it is what decides what
/// word-by-word timings look like, on which all post-processing then rests.
/// </summary>
/// <remarks>
/// Ported from <c>FluidAudioAdapterTests.swift</c>. Four of its tests have no subject left on
/// this engine and are not ported:
/// <list type="bullet">
/// <item><c>testMelChunkContextIsOffByDefault</c> and
/// <c>testChunkTokenCeilingIsRaisedAboveTheLibraryDefault</c> - both guard FluidAudio's
/// long-form windowing. sherpa-onnx does not window: it encodes the whole utterance in one
/// pass, so there is no window seam to lose speech at and no per-window token ceiling to
/// silently break out of.</item>
/// <item><c>testCancelledVocabularyRebuildStopsBeforeMutatingTheAdapter</c> and
/// <c>testScenario001</c>…<c>testScenario003</c>, <c>testScenario005</c>,
/// <c>testScenario006</c> - the acoustic term hint. The engine does not offer it; the reason
/// is on <see cref="SherpaOnnxAdapter"/>, and the contract that remains is guarded by
/// <see cref="VocabularyHintIsReportedAsUnsupportedRatherThanIgnored"/>.</item>
/// <item><c>testScenario004</c> - the preview start/stop race. There is no preview to race
/// with; <see cref="PreviewIsSkippedRatherThanFaked"/> guards what is left of the contract.</item>
/// <item><c>testAdapterOwnsOfflineModeBeforeLoading</c> - FluidAudio has a global
/// "may I download models" switch that the adapter forces off. sherpa-onnx has no such
/// switch and no downloader: it opens the paths it is handed and nothing else.</item>
/// </list>
/// <para>
/// There is deliberately no test for "the files exist but hold garbage". sherpa-onnx does not
/// report that failure - it prints to stderr and terminates the process from native code
/// (<c>symbol-table.cc:ReadTokens</c> was observed doing exactly that), so there is no
/// exception to catch and no assertion to make. The barrier that actually protects the user
/// is one layer up: <c>ModelStore</c> verifies every file against the manifest's SHA-256
/// before an installation is ever handed to the engine.
/// </para>
/// </remarks>
public class SherpaOnnxAdapterTests
{
    private static TokenTiming Timing(string token, double start, double end, double? confidence = 1) =>
        new(token, start, end, confidence);

    [Fact]
    public void EmptyTimingsProduceNoWords()
    {
        Assert.Empty(SherpaOnnxAdapter.Words(null));
        Assert.Empty(SherpaOnnxAdapter.Words([]));
    }

    [Fact]
    public void JoinsSubwordTokensIntoSingleWord()
    {
        // Parakeet cuts words into subwords: the start of a word is marked with "▁", the
        // continuation carries no marker.
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing("▁при", 0.0, 0.2),
            Timing("вет", 0.2, 0.4),
            Timing("▁мир", 0.5, 0.7),
        ]);

        Assert.Equal(["привет", "мир"], words.Select(word => word.Text));
        Assert.Equal(TimeSpan.Zero, words[0].Start);
        // The end of a word is the end of its last token, not of its first.
        Assert.Equal(TimeSpan.FromSeconds(0.4), words[0].End);
        Assert.Equal(TimeSpan.FromSeconds(0.5), words[1].Start);
    }

    [Fact]
    public void HandlesLeadingSpaceStyleTokens()
    {
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing(" hello", 0.0, 0.3),
            Timing(" world", 0.4, 0.8),
        ]);

        Assert.Equal(["hello", "world"], words.Select(word => word.Text));
    }

    [Fact]
    public void WordConfidenceTakesTheWeakestToken()
    {
        // A word is only as reliable as its worst piece - otherwise confidence is overstated
        // exactly where the model hesitated.
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing("▁дик", 0.0, 0.2, confidence: 0.95),
            Timing("тов", 0.2, 0.3, confidence: 0.40),
            Timing("ка", 0.3, 0.4, confidence: 0.90),
        ]);

        Assert.Single(words);
        Assert.Equal("диктовка", words[0].Text);
        Assert.Equal(0.40, Assert.IsType<double>(words[0].Confidence), 3);
    }

    /// <summary>
    /// sherpa-onnx reports no per-token confidence at all, and a word assembled from pieces
    /// nobody scored must say "unknown" rather than borrow a number from the pieces that were
    /// scored. This is the case the real engine actually hits on every dictation.
    /// </summary>
    [Fact]
    public void UnknownTokenConfidenceMakesTheWholeWordUnknown()
    {
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing("▁дик", 0.0, 0.2, confidence: 0.95),
            Timing("товка", 0.2, 0.4, confidence: null),
        ]);

        Assert.Single(words);
        Assert.Null(words[0].Confidence);
    }

    [Fact]
    public void SkipsEmptyTokens()
    {
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing("▁", 0.0, 0.1),
            Timing("▁тест", 0.1, 0.3),
            Timing("  ", 0.3, 0.4),
        ]);

        Assert.Equal(["тест"], words.Select(word => word.Text));
    }

    [Fact]
    public void TimingsStayMonotonic()
    {
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing("▁раз", 0.0, 0.3),
            Timing("▁два", 0.35, 0.6),
            Timing("▁три", 0.7, 1.0),
        ]);

        for (int index = 1; index < words.Count; index++)
        {
            Assert.True(
                words[index - 1].End <= words[index].Start,
                "words must not overlap each other");
        }
    }

    [Fact]
    public void MixedRussianEnglishKeepsLatinIntact()
    {
        // The main product scenario: English terms inside Russian speech must not be glued to
        // their neighbours.
        IReadOnlyList<ASRResult.Word> words = SherpaOnnxAdapter.Words([
            Timing("▁закинул", 0.0, 0.4),
            Timing("▁pull", 0.5, 0.7),
            Timing("▁request", 0.75, 1.1),
        ]);

        Assert.Equal(["закинул", "pull", "request"], words.Select(word => word.Text));
    }

    // MARK: - What the engine's numbers mean

    [Fact]
    public void TokenExtentComesFromItsTdtDuration()
    {
        IReadOnlyList<TokenTiming> timings = SherpaOnnxAdapter.Timings(
            ["▁one", "▁two"],
            [0.0f, 0.8f],
            [0.24f, 0.4f]);

        Assert.Equal(0.0, timings[0].StartTime, 3);
        Assert.Equal(0.24, timings[0].EndTime, 3);
        Assert.Equal(1.2, timings[1].EndTime, 3);
        // Nothing in the engine reports a score, and inventing one would be worse than none.
        Assert.All(timings, timing => Assert.Null(timing.Confidence));
    }

    /// <summary>
    /// Without durations the next token's start is the best end available, and the last token
    /// has no extent to claim.
    /// </summary>
    [Fact]
    public void MissingDurationsFallBackToTheNextTokenStart()
    {
        IReadOnlyList<TokenTiming> timings = SherpaOnnxAdapter.Timings(
            ["▁one", "▁two"],
            [0.0f, 0.8f],
            []);

        Assert.Equal(0.8, timings[0].EndTime, 3);
        Assert.Equal(0.8, timings[1].EndTime, 3);
    }

    /// <summary>
    /// Measured, not imagined: on <c>modified_beam_search</c> this engine returns timestamps
    /// that are not seconds at all. A dictation must not end in an arithmetic overflow because
    /// of it - the text is still the truth, and unknown word boundaries are a case
    /// <c>ASRResult</c> already has an answer for.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    [InlineData(1e12f)]
    public void ImpossibleTimestampsDropTheTimingsInsteadOfOverflowing(float broken)
    {
        IReadOnlyList<TokenTiming> timings = SherpaOnnxAdapter.Timings(
            ["▁one", "▁two"],
            [0.0f, broken],
            [0.2f, 0.2f]);

        Assert.Empty(timings);
        Assert.Empty(SherpaOnnxAdapter.Words(timings));
    }

    [Fact]
    public void TimestampsShorterThanTheTokenListDropTheTimings()
    {
        Assert.Empty(SherpaOnnxAdapter.Timings(["▁one", "▁two"], [0.0f], [0.2f]));
    }

    // MARK: - Engine knobs

    /// <summary>
    /// The analogue of the Swift test that guards the encoder file variant. The published,
    /// mirrored and documented export is the quantized one; the unquantized set exists only
    /// so the two can be measured against each other, and it is not what ships.
    /// </summary>
    [Fact]
    public void QuantizedEncoderIsTheDefault()
    {
        var adapter = new SherpaOnnxAdapter();

        Assert.Equal(EncoderPrecision.Int8, adapter.Encoder);
    }

    /// <summary>
    /// The two file sets are genuinely different sets, not the same names in two folders. The
    /// unquantized encoder keeps its weights outside the graph, so a folder holding only
    /// <c>encoder.onnx</c> is a half-installation.
    /// </summary>
    [Fact]
    public void EachPrecisionAsksForItsOwnFiles()
    {
        IReadOnlyList<string> quantized = SherpaOnnxAdapter.RequiredFiles(EncoderPrecision.Int8);
        IReadOnlyList<string> full = SherpaOnnxAdapter.RequiredFiles(EncoderPrecision.Float32);

        Assert.Contains("encoder.int8.onnx", quantized);
        Assert.DoesNotContain("encoder.onnx", quantized);
        Assert.Contains("encoder.weights", full);
        Assert.Contains("tokens.txt", quantized);
        Assert.Contains("tokens.txt", full);
    }

    /// <summary>
    /// The analogue of the Swift test that guards where the encoder runs. On Windows the
    /// predictable engine is the CPU one: CUDA needs a matching driver stack the installer
    /// does not ship, and DirectML support depends on the exported graph. Both stay available
    /// for measurement, neither is chosen behind the user's back.
    /// </summary>
    [Fact]
    public void EncoderRunsOnTheCpuByDefault()
    {
        var adapter = new SherpaOnnxAdapter();

        Assert.Equal("cpu", adapter.Provider);
    }

    [Fact]
    public void DecodingIsGreedyByDefault()
    {
        var adapter = new SherpaOnnxAdapter();

        Assert.Equal("greedy_search", adapter.DecodingMethod);
    }

    /// <summary>
    /// The analogue of the Swift test that guards the long-form window pool. There are no
    /// windows to spread here, so what is left is the runtime's own intra-op pool; a
    /// zero or negative count would silently mean "as many as you like" in the native layer.
    /// </summary>
    [Fact]
    public void ThreadCountIsPositive()
    {
        Assert.Equal(4, new SherpaOnnxAdapter().Threads);
        Assert.Equal(1, new SherpaOnnxAdapter(numThreads: 0).Threads);
        Assert.Equal(1, new SherpaOnnxAdapter(numThreads: -8).Threads);
    }

    // MARK: - Errors that must reach the user

    /// <summary>
    /// A folder without the ONNX files is a loading error, not a silent "recognition will not
    /// work". There is no network here: the failure happens while checking files.
    /// </summary>
    [Fact]
    public async Task EmptyDirectoryIsReportedAsAnIncompleteModel()
    {
        var adapter = new SherpaOnnxAdapter();
        string empty = Path.Combine(Path.GetTempPath(), $"onnx-empty-{Guid.NewGuid()}");
        Directory.CreateDirectory(empty);
        try
        {
            ASREngineError.ModelsUnavailable error =
                await Assert.ThrowsAsync<ASREngineError.ModelsUnavailable>(() => adapter.LoadModels(empty));

            Assert.Contains("doesn't contain the full set", error.Reason, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(empty), error.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>
    /// A folder with the quantized set is not a folder with the unquantized one. Loading it as
    /// the wrong variant must fail on the file check, not deep inside the runtime.
    /// </summary>
    [Fact]
    public async Task WrongPrecisionForTheInstalledFilesIsRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"onnx-int8-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string name in SherpaOnnxAdapter.RequiredFiles(EncoderPrecision.Int8))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, name), "not really a model");
            }

            var adapter = new SherpaOnnxAdapter(EncoderPrecision.Float32);

            await Assert.ThrowsAsync<ASREngineError.ModelsUnavailable>(() => adapter.LoadModels(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TranscribingWithoutAModelIsReportedRatherThanGuessed()
    {
        var adapter = new SherpaOnnxAdapter();

        await Assert.ThrowsAsync<ASREngineError.ModelsNotLoaded>(() => adapter.Transcribe([0.1f, 0.2f]));
    }

    /// <summary>
    /// An unknown language code is the caller's error, and it is visible immediately, before
    /// the models are even consulted: it has no right to silently become "auto".
    /// </summary>
    [Fact]
    public async Task UnknownLanguageHintIsAnError()
    {
        var adapter = new SherpaOnnxAdapter();

        ASREngineError.InferenceFailed error = await Assert.ThrowsAsync<ASREngineError.InferenceFailed>(
            () => adapter.Transcribe([0.1f, 0.2f], "xx"));

        Assert.Contains("xx", error.Reason, StringComparison.Ordinal);
    }

    /// <summary>The list of hints is the source for the language picker in the interface.</summary>
    [Fact]
    public void SupportedLanguageHintsAreUsableAsAPicker()
    {
        IReadOnlyList<string> hints = SherpaOnnxAdapter.SupportedLanguageHints;

        Assert.Contains("ru", hints);
        Assert.Contains("en", hints);
        Assert.Equal(hints.Count, hints.ToHashSet(StringComparer.Ordinal).Count);
    }

    // MARK: - The optional abilities this engine does not have

    /// <summary>
    /// A person who has terms in their dictionary must not be told the acoustic hint is on
    /// when nothing is hinting. <see cref="LocalTranscriber"/> probes for the ability and
    /// refuses out loud; the engine's job is to not claim it.
    /// </summary>
    [Fact]
    public async Task VocabularyHintIsReportedAsUnsupportedRatherThanIgnored()
    {
        var adapter = new SherpaOnnxAdapter();
        var transcriber = new LocalTranscriber(adapter);

        Assert.IsNotAssignableFrom<IVocabularyBoostCapable>(adapter);
        await Assert.ThrowsAsync<ASREngineError.ModelsUnavailable>(
            () => transcriber.PrepareVocabulary("/nonexistent", VocabularyBoost.DeveloperDefault()));
    }

    /// <summary>
    /// The preview is decoration on top of the dictation, not a part of it: an engine that
    /// cannot show one is skipped silently, and the dictation is unaffected. Faking it by
    /// re-recognizing a growing buffer would compete for the very CPU the person is waiting on.
    /// </summary>
    [Fact]
    public async Task PreviewIsSkippedRatherThanFaked()
    {
        var adapter = new SherpaOnnxAdapter();
        var transcriber = new LocalTranscriber(adapter);

        Assert.IsNotAssignableFrom<ILivePreviewCapable>(adapter);
        await transcriber.StartPreview((_, _) => Assert.Fail("there is no preview to update"));
        await transcriber.FeedPreview(new float[16_000]);
        await transcriber.StopPreview();
    }

    // MARK: - Memory residency

    /// <summary>
    /// Unloading something that was never loaded is not an error: residency management calls
    /// it on a schedule, and wedge recovery calls it blind.
    /// </summary>
    [Fact]
    public async Task UnloadWithoutLoadIsHarmless()
    {
        var adapter = new SherpaOnnxAdapter();

        await adapter.Unload();

        Assert.False(adapter.IsLoaded);
    }

    // MARK: - The one test that needs the real model

    /// <summary>
    /// The whole chain on real weights: WAV off the disk, resampled to 16 kHz, through the
    /// engine, into text and word timings.
    ///
    /// Gated on <c>OPENRAMBLE_MODEL_DIR</c> - the same variable <c>asr-bench</c> uses for a
    /// frozen engine folder - so an ordinary <c>dotnet test</c> neither downloads 670 MB nor
    /// fails for the want of it.
    /// </summary>
    [InstalledModelFact]
    public async Task TranscribesTheFixtureWithTheInstalledModel()
    {
        string directory = InstalledModelFactAttribute.Directory!;
        var adapter = new SherpaOnnxAdapter();
        var transcriber = new LocalTranscriber(adapter);
        await transcriber.Prepare(directory);
        try
        {
            Assert.True(adapter.IsLoaded);

            ASRResult result = await transcriber.Transcribe(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "en.wav"));

            // The fixture says: "Ask not what your country can do for you. Ask what you can
            // do for your country." Asserting the words rather than the string keeps the test
            // about recognition instead of about punctuation and casing.
            string[] heard = result.Text
                .ToLowerInvariant()
                .Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("country", heard);
            Assert.Contains("ask", heard);
            Assert.True(heard.Length > 12, $"suspiciously few words: {heard.Length}");

            // 3.85 s of audio at 24 kHz, resampled to 16 kHz on the way in - the resampler
            // may land a sample either side of the exact ratio, so the window is a window.
            Assert.InRange(result.AudioDuration.TotalSeconds, 3.84, 3.85);
            Assert.NotEmpty(result.Words);
            Assert.All(result.Words, word => Assert.True(word.End >= word.Start));
            Assert.True(
                result.Words[^1].End <= result.AudioDuration,
                "a word cannot end after the recording does");
        }
        finally
        {
            await transcriber.Unload();
        }

        Assert.False(adapter.IsLoaded);
    }
}
