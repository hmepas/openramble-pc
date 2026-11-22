namespace DictationCore;

/// <summary>
/// The result of recognizing one fragment of speech.
///
/// The type is declared here, and not in LocalASR, intentionally: pure logic should be able to
/// work with the result without knowing anything about which engine produced it.
/// </summary>
public sealed record ASRResult
{
    public ASRResult(
        string text,
        IReadOnlyList<Word> words,
        TimeSpan audioDuration,
        TimeSpan processingDuration,
        TimeSpan engineDispatchDuration = default,
        TimeSpan queueingDuration = default,
        TimeSpan decodingDuration = default)
    {
        Text = text;
        Words = words;
        AudioDuration = audioDuration;
        ProcessingDuration = processingDuration;
        EngineDispatchDuration = engineDispatchDuration;
        QueueingDuration = queueingDuration;
        DecodingDuration = decodingDuration;
    }

    /// <summary>An empty word list is acceptable: not every engine provides timings.</summary>
    public ASRResult(
        string text,
        TimeSpan audioDuration,
        TimeSpan processingDuration,
        TimeSpan engineDispatchDuration = default,
        TimeSpan queueingDuration = default,
        TimeSpan decodingDuration = default)
        : this(text, [], audioDuration, processingDuration, engineDispatchDuration, queueingDuration, decodingDuration)
    {
    }

    /// <summary>One word with time boundaries relative to the beginning of the fragment.</summary>
    public sealed record Word(string Text, TimeSpan Start, TimeSpan End, double? Confidence = null);

    /// <summary>The finished text without post-processing is exactly what the engine heard.</summary>
    public string Text { get; }

    /// <summary>Word-by-word timings. An empty list is acceptable: not every engine provides them.</summary>
    public IReadOnlyList<Word> Words { get; }

    /// <summary>Duration of recognized audio.</summary>
    public TimeSpan AudioDuration { get; }

    /// <summary>Wall time spent inside the engine runtime call itself.</summary>
    public TimeSpan ProcessingDuration { get; }

    /// <summary>
    /// Time around the runtime call: admission to the engine's execution queue plus the
    /// continuation's return to its owner.
    /// </summary>
    public TimeSpan EngineDispatchDuration { get; }

    /// <summary>
    /// How long the call waited before the engine began.
    ///
    /// This is the wait outside the engine's own execution queue: admission to the transcriber,
    /// warm-up ownership and another dictation holding it. <see cref="EngineDispatchDuration"/>
    /// accounts for the queue immediately around the runtime call, so the two must remain
    /// separate.
    /// </summary>
    public TimeSpan QueueingDuration { get; }

    /// <summary>
    /// How long opening and decoding the recording took.
    ///
    /// Split out from the wait because it is the part that turned out to matter, and because a
    /// lump that says "something before the engine" is what let this hide for months.
    /// </summary>
    public TimeSpan DecodingDuration { get; }

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(ASRResult? other) =>
        other is not null
        && Text == other.Text
        && Words.SequenceEqual(other.Words)
        && AudioDuration == other.AudioDuration
        && ProcessingDuration == other.ProcessingDuration
        && EngineDispatchDuration == other.EngineDispatchDuration
        && QueueingDuration == other.QueueingDuration
        && DecodingDuration == other.DecodingDuration;

    public override int GetHashCode() =>
        HashCode.Combine(Text, Words.Count, AudioDuration, ProcessingDuration);
}

/// <summary>
/// Recognition engine errors.
///
/// Not a single case is silent: each branch must reach the user.
/// </summary>
/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; here the cases
/// are nested exception types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class ASREngineError : Exception
{
    private protected ASREngineError(string message)
        : base(message)
    {
    }

    /// <summary>The model is not loaded - a call to transcribe without loading.</summary>
    public sealed class ModelsNotLoaded : ASREngineError
    {
        public ModelsNotLoaded()
            : base("models are not loaded")
        {
        }
    }

    /// <summary>Model files are missing or damaged.</summary>
    public sealed class ModelsUnavailable : ASREngineError
    {
        public ModelsUnavailable(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>
    /// The engine's native runtime cannot start on this machine: a library missing, a processor
    /// it was not built for, or a build that does not match this app. The model files are not at
    /// fault, and downloading them again changes nothing.
    /// </summary>
    /// <remarks>Port addition: the Swift engines ship inside the app bundle and cannot go missing.</remarks>
    public sealed class RuntimeUnavailable : ASREngineError
    {
        public RuntimeUnavailable(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>Audio is not in the format expected by the engine.</summary>
    public sealed class UnsupportedAudioFormat : ASREngineError
    {
        public UnsupportedAudioFormat(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>The engine worked, but fell over inside.</summary>
    public sealed class InferenceFailed : ASREngineError
    {
        public InferenceFailed(string reason, int? code = null)
            : base(reason)
        {
            Reason = reason;
            Code = code;
        }

        public string Reason { get; }

        /// <summary>
        /// The engine's own numeric status, when it gave one. The only part of this error the
        /// local journal may keep: the reason can carry a path or text.
        /// </summary>
        public int? Code { get; }
    }

    /// <summary>Recognition has been cancelled by the user.</summary>
    public sealed class Cancelled : ASREngineError
    {
        public Cancelled()
            : base("cancelled")
        {
        }
    }
}

/// <summary>
/// Recognition engine contract.
///
/// The only implementation in the project lives in the LocalASR package,
/// and this is the only place where the engine library is imported. Everything else - including
/// tests of pure logic - works through this interface and uses a mock.
///
/// There is no language parameter here, and the absence is a measurement rather than an
/// oversight: the shipping engine decides the language from the audio and cannot be told
/// otherwise. Measured against the shipping model, asking for one language, asking for
/// another and letting the model decide all return identical transcripts - while a code the
/// model did not recognise would have failed the dictation outright.
/// </summary>
public interface IASREngineAdapting
{
    /// <summary>Load the model from the prepared directory. Idempotent.</summary>
    Task LoadModels(string directory);

    /// <summary>Recognize the fragment. Mono 16 kHz Float32 is expected - exactly what the capture gives.</summary>
    Task<ASRResult> Transcribe(float[] samples);

    /// <summary>Free up the memory under the model.</summary>
    Task Unload();

    /// <summary>
    /// Words the person taught, which the engine may lean toward while it decodes. An engine with
    /// no such mechanism ignores them.
    /// </summary>
    Task SetVocabulary(IReadOnlyList<string> terms) => Task.CompletedTask;
}
