using System.Diagnostics;
using DictationCore;
using SherpaOnnx;

namespace LocalASR;

/// <summary>
/// Which encoder file set to load.
/// </summary>
/// <remarks>
/// The macOS original picks between two Core ML encoder bundles
/// (<c>Encoder.mlmodelc</c> / <c>EncoderInt4.mlmodelc</c>). The ONNX publications are split the
/// same way, only by file name: the released set is quantized
/// (<c>encoder.int8.onnx</c> and friends), and there is an unquantized export whose encoder is
/// split into <c>encoder.onnx</c> plus a 2.4 GB external-data blob. As in Swift, the name is
/// the library's file variant, not a description of the arithmetic: the int8 encoder is
/// <c>QUInt8</c> while its decoder and joiner are <c>QInt8</c>.
/// </remarks>
public enum EncoderPrecision
{
    /// <summary>The published, mirrored, documented set. The only one k2-fsa ships as a release asset.</summary>
    Int8,

    /// <summary>The unquantized export. Kept so the two can be measured against each other.</summary>
    Float32,

    /// <summary>
    /// Our own build: the fp32 export with the encoder's matrix weights in 4-bit blocks
    /// (<c>MatMulNBits</c>), activations left in fp32. Made by
    /// <c>scripts/quantize-parakeet-int4.py</c>.
    /// </summary>
    /// <remarks>
    /// The int8 export is dynamically quantized - the activations, the convolutional front end
    /// included, are cut to 8 bits at run time - and on a quiet laptop microphone it answered
    /// Russian speech with nothing or with English filler while fp32 read the same take. This
    /// keeps fp32's accuracy at int8's size and speed: on FLEURS ru dev, WER 5.5% against int8's
    /// 7.3% and fp32's 5.4%.
    /// </remarks>
    Int4WeightOnly,
}

/// <summary>
/// One decoded token with its place in the recording.
/// </summary>
/// <remarks>
/// FluidAudio hands the Swift adapter its own <c>TokenTiming</c>; sherpa-onnx returns three
/// parallel arrays (<c>Tokens</c>, <c>Timestamps</c>, <c>Durations</c>). This type is where
/// they meet, so that the token→word assembly stays the same function it is in Swift and can
/// be tested without a model.
///
/// <c>tokenId</c> from the Swift type has no counterpart - the C# bindings expose token text
/// only - and nothing read it, so it is not invented here.
/// </remarks>
/// <param name="Token">The raw subword, including the leading "▁" when it opens a word.</param>
/// <param name="StartTime">Seconds from the start of the fragment.</param>
/// <param name="EndTime">Seconds from the start of the fragment.</param>
/// <param name="Confidence">
/// <c>null</c> when the engine does not report one. sherpa-onnx never does: the offline
/// transducer result carries text, tokens, timestamps and TDT durations, and no score.
/// </param>
public sealed record TokenTiming(string Token, double StartTime, double EndTime, double? Confidence = null);

/// <summary>
/// The only place in the entire project where sherpa-onnx is imported.
///
/// Everything else - including the dictation controller and its tests - works through
/// <see cref="IASREngineAdapting"/> from DictationCore. If the library API moves, only this
/// file has to be repaired.
/// </summary>
/// <remarks>
/// <para>
/// Two optional engine abilities the Swift adapter conforms to are deliberately <b>not</b>
/// implemented here, because sherpa-onnx cannot honestly back them:
/// </para>
/// <para>
/// <see cref="ILivePreviewCapable"/> - FluidAudio ships a pseudo-streaming manager
/// (<c>SlidingWindowAsrManager</c>) that reuses the loaded batch weights. sherpa-onnx has no
/// counterpart for an <i>offline</i> model: its streaming recognizer accepts only models
/// exported for streaming, and Parakeet TDT v3 is not one. The only way to fake a preview
/// would be to re-run whole-utterance recognition on a growing buffer several times a second -
/// which is not the same computation, costs multiples of the dictation's own CPU, and would
/// make the preview compete with the recognition the user is actually waiting for.
/// <see cref="LocalTranscriber"/> already treats preview as optional decoration and skips it
/// silently, so the absence degrades nothing.
/// </para>
/// <para>
/// <see cref="IVocabularyBoostCapable"/> - sherpa-onnx does have contextual biasing
/// ("hotwords"), but it cannot carry <see cref="VocabularyBoost"/>'s contract. Three reasons,
/// each sufficient: (1) hotwords raise the score of the term's <i>own</i> spelling, so a
/// Cyrillic alias would put the alias into the text rather than pull the canonical Latin term
/// out of it - the alias bridge is the whole point of the type; (2) biasing only exists in
/// the <c>modified_beam_search</c> branch and needs a <c>bpe.vocab</c> file that the published
/// model repository does not contain (it has to be regenerated from the original
/// <c>.nemo</c> checkpoint); (3) there is no similarity threshold to honour -
/// <see cref="VocabularyBoost.MinSimilarity"/> describes a rescoring pass over CTC evidence,
/// not a beam bonus. Declaring the interface anyway would make
/// <see cref="LocalTranscriber.PrepareVocabulary"/> report success for a feature that does
/// something else.
/// </para>
/// </remarks>
public sealed class SherpaOnnxAdapter : IASREngineAdapting
{
    /// <summary>
    /// Guards the recognizer handle and the count of decodes running on it.
    /// </summary>
    /// <remarks>
    /// The Swift original is an <c>actor</c>, and actors are reentrant: its <c>unload</c> may
    /// land between two suspensions of a running recognition. That is survivable there because
    /// the library owns its own memory. Here the handle is native, so freeing it under a
    /// running decode would not be a nondeterministic state but a crash. The lock therefore
    /// protects the handle, never the inference: every decode runs outside it, on a snapshot,
    /// and disposal waits for the last decode to leave. An unload asked for during a wedged
    /// inference still returns at once - the engine is unreachable from that moment - and the
    /// weights are released when the zombie finally returns, which is exactly what
    /// <see cref="LocalTranscriber.Unload"/> promises its caller.
    /// </remarks>
    private readonly Lock gate = new();

    private OfflineRecognizer? recognizer;

    /// <summary>Decodes in flight on <see cref="recognizer"/> or on an already-orphaned handle.</summary>
    private int activeDecodes;

    /// <summary>A handle taken out of service that is only waiting for its last decode to finish.</summary>
    private OfflineRecognizer? retiring;

    private readonly EncoderPrecision precision;
    private readonly int numThreads;
    private readonly string provider;
    private readonly string decodingMethod;

    /// <summary>
    /// Threads inside one recognition.
    ///
    /// The Swift adapter's analogue is <c>parallelChunkConcurrency</c>: FluidAudio splits a
    /// long recording into windows and runs several at once. sherpa-onnx has no windows to
    /// spread - it decodes the whole utterance in one pass - so the parallelism that is left
    /// is the ONNX Runtime's own intra-op pool. Four is the library's own documented default
    /// for offline recognition.
    /// </summary>
    public const int DefaultNumThreads = 4;

    /// <summary>
    /// Greedy search, like the shipping macOS path.
    ///
    /// <c>modified_beam_search</c> exists here so the bench can measure it rather than argue
    /// about it; it is not the default, because on a TDT transducer it costs time per token
    /// and buys nothing that has been measured.
    /// </summary>
    public const string DefaultDecodingMethod = "greedy_search";

    public SherpaOnnxAdapter(
        EncoderPrecision precision = EncoderPrecision.Int8,
        int numThreads = DefaultNumThreads,
        string provider = "cpu",
        string decodingMethod = DefaultDecodingMethod)
    {
        this.precision = precision;
        this.numThreads = Math.Max(1, numThreads);
        this.provider = provider;
        this.decodingMethod = decodingMethod;
    }

    /// <summary>For the test that guards the shipped encoder variant.</summary>
    internal EncoderPrecision Encoder => precision;

    /// <summary>For the test that guards the shipped thread count.</summary>
    internal int Threads => numThreads;

    /// <summary>For the test that guards the shipped execution provider.</summary>
    internal string Provider => provider;

    /// <summary>For the test that guards the shipped decoding method.</summary>
    internal string DecodingMethod => decodingMethod;

    /// <summary>Is the model loaded right now? Test seam for the load/unload cycle.</summary>
    internal bool IsLoaded
    {
        get
        {
            lock (gate)
            {
                return recognizer is not null;
            }
        }
    }

    /// <summary>
    /// The files one encoder variant needs, in the order the checker reports them missing.
    /// </summary>
    /// <remarks>
    /// The unquantized export keeps the encoder weights outside the graph, in
    /// <c>encoder.weights</c>: ONNX Runtime loads it by the name recorded in the graph, from
    /// the same directory. It is listed here because a directory holding <c>encoder.onnx</c>
    /// without it is exactly the half-installation this check exists to reject.
    /// </remarks>
    internal static IReadOnlyList<string> RequiredFiles(EncoderPrecision precision) => precision switch
    {
        EncoderPrecision.Int8 => ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"],
        EncoderPrecision.Int4WeightOnly => ["encoder.int4.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"],
        _ => ["encoder.onnx", "encoder.weights", "decoder.onnx", "joiner.onnx", "tokens.txt"],
    };

    /// <summary>
    /// Load the model from the prepared directory.
    ///
    /// There is no network here: the recognizer is built from files that are already on disk.
    /// OpenRamble controls the model itself - the user explicitly downloads the committed
    /// manifest, after which every file is checked by SHA-256 - and nothing in sherpa-onnx
    /// fetches anything it was not handed a path to.
    /// </summary>
    /// <remarks>
    /// The check here is that the names are all present, and it stops there on purpose: given
    /// a file that exists but holds garbage, sherpa-onnx does not return an error - it writes
    /// to stderr and terminates the process from native code. Content integrity is therefore
    /// <c>ModelStore</c>'s job, and it does it before this method is ever called, by checking
    /// every file against the manifest's SHA-256. What this gate catches is the case that
    /// check cannot: a directory that is simply not a model directory.
    /// </remarks>
    public async Task LoadModels(string directory)
    {
        lock (gate)
        {
            if (recognizer is not null)
            {
                return;
            }
        }

        IReadOnlyList<string> required = RequiredFiles(precision);
        if (required.Any(name => !File.Exists(Path.Combine(directory, name))))
        {
            throw new ASREngineError.ModelsUnavailable(
                $"{Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}"
                + " doesn't contain the full set of Parakeet v3 ONNX files");
        }

        OfflineRecognizer built;
        try
        {
            // Building the recognizer reads and specializes ~670 MB of graphs; on the UI thread
            // that is a frozen window, so it happens on the pool. The Swift original gets this
            // for free - `AsrModels.load` is genuinely async there.
            built = await Task.Run(() => new OfflineRecognizer(Configure(directory))).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ASREngineError)
        {
            throw new ASREngineError.ModelsUnavailable(error.Message);
        }

        bool superseded;
        lock (gate)
        {
            superseded = recognizer is not null;
            if (!superseded)
            {
                recognizer = built;
            }
        }

        if (superseded)
        {
            // Two loads raced and the other one won. Idempotence is the contract; a second
            // 670 MB of native memory is not.
            built.Dispose();
        }
    }

    private OfflineRecognizerConfig Configure(string directory)
    {
        bool quantized = precision == EncoderPrecision.Int8;
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(directory, precision switch
        {
            EncoderPrecision.Int8 => "encoder.int8.onnx",
            EncoderPrecision.Int4WeightOnly => "encoder.int4.onnx",
            _ => "encoder.onnx",
        });
        config.ModelConfig.Transducer.Decoder =
            Path.Combine(directory, quantized ? "decoder.int8.onnx" : "decoder.onnx");
        config.ModelConfig.Transducer.Joiner =
            Path.Combine(directory, quantized ? "joiner.int8.onnx" : "joiner.onnx");
        config.ModelConfig.Tokens = Path.Combine(directory, "tokens.txt");
        // Required. sherpa-onnx cannot tell a NeMo transducer from an icefall one by the files
        // alone, and the wrong guess is a wrong answer rather than an error. The TDT part is
        // not a separate type: the library reads the encoder's own metadata and switches to
        // the TDT decoder when it finds "tdt" in the recorded model URL.
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = numThreads;
        config.ModelConfig.Provider = provider;
        config.DecodingMethod = decodingMethod;
        config.FeatConfig.SampleRate = (int)AudioFileReader.TargetSampleRate;
        // Parakeet v3 uses 128 mel bins, not the 80 that the generic documentation page
        // mentions. The value is written here for the record only: the library overrides it
        // from the encoder's own metadata, and the same metadata carries the NeMo
        // per-feature normalization the model was trained with.
        config.FeatConfig.FeatureDim = 128;
        return config;
    }

    /// <summary>
    /// Languages the engine accepts as hints (BCP-47 codes).
    ///
    /// The list is a property of the engine, not of the product, so it lives on the only
    /// sherpa-onnx import boundary. The UI builds the language picker from it.
    /// </summary>
    /// <remarks>
    /// The 25 European languages Parakeet TDT 0.6B v3 was trained on; the model's own
    /// <c>tokens.txt</c> carries exactly these as <c>&lt;|xx|&gt;</c> tags.
    /// </remarks>
    public static IReadOnlyList<string> SupportedLanguageHints { get; } =
    [
        "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hr", "hu", "it",
        "lt", "lv", "mt", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "uk",
    ];

    public Task<ASRResult> Transcribe(float[] samples) => Transcribe(samples, null);

    /// <summary>
    /// Recognize the fragment.
    /// </summary>
    /// <remarks>
    /// <paramref name="languageHint"/> is validated and then <b>ignored</b>, and both halves
    /// are deliberate. Validated, because an unknown code is a caller's mistake and it must be
    /// visible instead of silently becoming "auto" - that is the behaviour the Swift tests
    /// pin down. Ignored, because sherpa-onnx's offline recognizer has no language input at
    /// all for a NeMo transducer: the model detects the language from the sound itself, and
    /// there is no configuration field, no stream option, and no decoder argument to override
    /// it with. <see cref="IASREngineAdapting.Transcribe(float[], string?)"/> calls the hint
    /// optional for exactly this case - "an engine that does not understand it recognizes as
    /// usual" - so a person who picked a language still gets their dictation, and the picker
    /// simply does not shift the odds the way it does on macOS.
    /// </remarks>
    public async Task<ASRResult> Transcribe(float[] samples, string? languageHint)
    {
        // The hint is checked before everything else: an unknown code is the caller's error,
        // and it must be visible rather than silently becoming "auto".
        if (languageHint is not null && !SupportedLanguageHints.Contains(languageHint))
        {
            throw new ASREngineError.InferenceFailed($"unsupported language hint: {languageHint}");
        }

        OfflineRecognizer engine;
        lock (gate)
        {
            if (recognizer is null)
            {
                throw new ASREngineError.ModelsNotLoaded();
            }

            if (samples.Length == 0)
            {
                throw new ASREngineError.UnsupportedAudioFormat("empty buffer");
            }

            engine = recognizer;
            activeDecodes++;
        }

        try
        {
            long started = Stopwatch.GetTimestamp();
            // The duration is computed here rather than taken from the engine: sherpa-onnx does
            // not report it, and "how many times faster than real time" depends on it.
            var audioDuration = TimeSpan.FromSeconds(samples.Length / AudioFileReader.TargetSampleRate);

            IReadOnlyList<TokenTiming> timings;
            string text;
            try
            {
                (text, timings) = await Task.Run(() => Decode(engine, samples)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not ASREngineError)
            {
                throw new ASREngineError.InferenceFailed(error.Message);
            }

            return new ASRResult(
                text,
                Words(timings),
                audioDuration,
                Stopwatch.GetElapsedTime(started));
        }
        finally
        {
            OfflineRecognizer? orphan = null;
            lock (gate)
            {
                activeDecodes--;
                if (activeDecodes == 0 && retiring is not null)
                {
                    orphan = retiring;
                    retiring = null;
                }
            }

            orphan?.Dispose();
        }
    }

    /// <summary>
    /// One whole-utterance pass. No windowing: unlike FluidAudio, sherpa-onnx feeds the entire
    /// buffer to the encoder in a single pass, so there are no seams to deduplicate and no
    /// per-window token ceiling that could silently swallow the middle of a phrase.
    /// </summary>
    private static (string Text, IReadOnlyList<TokenTiming> Timings) Decode(
        OfflineRecognizer engine,
        float[] samples)
    {
        using OfflineStream stream = engine.CreateStream();
        stream.AcceptWaveform((int)AudioFileReader.TargetSampleRate, samples);
        engine.Decode(stream);
        OfflineRecognizerResult result = stream.Result;

        return (
            result.Text ?? string.Empty,
            Timings(result.Tokens ?? [], result.Timestamps ?? [], result.Durations ?? []));
    }

    /// <summary>
    /// Turn sherpa-onnx's three parallel arrays into tokens that know where they are.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Decode"/> so it can be tested without 670 MB of weights: it is
    /// the only place that decides what the engine's numbers mean.
    /// </remarks>
    internal static IReadOnlyList<TokenTiming> Timings(
        string[] tokens,
        float[] timestamps,
        float[] durations)
    {
        var timings = new List<TokenTiming>(tokens.Length);
        for (int index = 0; index < tokens.Length; index++)
        {
            // Timestamps are optional in the C API, and they are not always sane: the
            // modified_beam_search path on this model was measured returning values that are
            // not finite seconds at all. Without trustworthy timestamps the text is still the
            // truth; only the word boundaries are unknown, and an empty word list is exactly
            // what ASRResult expects in that case. Passing them on would end the dictation
            // with an overflow instead.
            if (index >= timestamps.Length || !IsSaneSecond(timestamps[index]))
            {
                timings.Clear();
                break;
            }

            double start = timestamps[index];
            // A TDT token carries the number of frames the decoder skipped after emitting it -
            // that is the token's own extent. Without it, the next token's start is the best
            // available end, and the last token gets no extent at all.
            double end = index < durations.Length && IsSaneSecond(durations[index])
                ? start + durations[index]
                : index + 1 < timestamps.Length && IsSaneSecond(timestamps[index + 1])
                    ? timestamps[index + 1]
                    : start;
            timings.Add(new TokenTiming(tokens[index], start, Math.Max(start, end)));
        }

        return timings;
    }

    /// <summary>
    /// Is this a number of seconds a recording could actually contain?
    /// </summary>
    /// <remarks>
    /// A day is far past any dictation, and anything beyond it - including infinity and NaN -
    /// is a broken reading rather than a long one.
    /// </remarks>
    private static bool IsSaneSecond(float value) =>
        float.IsFinite(value) && value >= 0 && value <= 24 * 60 * 60;

    /// <summary>
    /// Free the memory under the model.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the engine is unreachable. If a recognition is still running on it,
    /// the native handle is released by that recognition's own exit path - the alternative is
    /// freeing graphs out from under a running ONNX session.
    /// </remarks>
    public Task Unload()
    {
        OfflineRecognizer? orphan = null;
        lock (gate)
        {
            OfflineRecognizer? taken = recognizer;
            recognizer = null;
            if (taken is null)
            {
                return Task.CompletedTask;
            }

            if (activeDecodes == 0)
            {
                orphan = taken;
            }
            else
            {
                retiring = taken;
            }
        }

        orphan?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Glue word-by-word timings together out of tokens.
    ///
    /// Parakeet returns its result in tokens, not words: subwords continue without a leading
    /// marker, so a word boundary is a token that starts with one.
    /// </summary>
    internal static IReadOnlyList<ASRResult.Word> Words(IReadOnlyList<TokenTiming>? timings)
    {
        if (timings is null || timings.Count == 0)
        {
            return [];
        }

        var words = new List<ASRResult.Word>();
        (string Text, double Start, double End, double? Confidence)? current = null;

        foreach (TokenTiming timing in timings)
        {
            // The engine gives tokens with a leading "▁" or with an ordinary space - both mean
            // the beginning of a new word.
            string raw = timing.Token;
            bool startsWord = raw.StartsWith('▁') || raw.StartsWith(' ');
            string cleaned = raw.Replace("▁", string.Empty, StringComparison.Ordinal).Trim();

            if (cleaned.Length == 0)
            {
                continue;
            }

            if (startsWord && current is { } pending)
            {
                words.Add(new ASRResult.Word(
                    pending.Text,
                    TimeSpan.FromSeconds(pending.Start),
                    TimeSpan.FromSeconds(pending.End),
                    pending.Confidence));
                current = null;
            }

            if (current is { } growing)
            {
                current = (
                    growing.Text + cleaned,
                    growing.Start,
                    timing.EndTime,
                    Weakest(growing.Confidence, timing.Confidence));
            }
            else
            {
                current = (cleaned, timing.StartTime, timing.EndTime, timing.Confidence);
            }
        }

        if (current is { } last)
        {
            words.Add(new ASRResult.Word(
                last.Text,
                TimeSpan.FromSeconds(last.Start),
                TimeSpan.FromSeconds(last.End),
                last.Confidence));
        }

        return words;
    }

    /// <summary>
    /// A word is only as reliable as its worst piece - otherwise confidence is overstated
    /// exactly where the model hesitated.
    /// </summary>
    /// <remarks>
    /// Swift takes a plain <c>min</c> because FluidAudio always reports a number. Here a token
    /// may carry none, and an unknown piece makes the whole word unknown: reporting the
    /// remaining pieces' minimum would be claiming a measurement nobody made.
    /// </remarks>
    private static double? Weakest(double? left, double? right) =>
        left is { } first && right is { } second ? Math.Min(first, second) : null;
}
