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
        TimeSpan processingDuration)
    {
        Text = text;
        Words = words;
        AudioDuration = audioDuration;
        ProcessingDuration = processingDuration;
    }

    /// <summary>An empty word list is acceptable: not every engine provides timings.</summary>
    public ASRResult(string text, TimeSpan audioDuration, TimeSpan processingDuration)
        : this(text, [], audioDuration, processingDuration)
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

    /// <summary>How long the recognition itself took - for measurements and for display in diagnostics.</summary>
    public TimeSpan ProcessingDuration { get; }

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(ASRResult? other) =>
        other is not null
        && Text == other.Text
        && Words.SequenceEqual(other.Words)
        && AudioDuration == other.AudioDuration
        && ProcessingDuration == other.ProcessingDuration;

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
        public InferenceFailed(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
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
/// </summary>
public interface IASREngineAdapting
{
    /// <summary>Load the model from the prepared directory. Idempotent.</summary>
    Task LoadModels(string directory);

    /// <summary>Recognize the fragment. Mono 16 kHz Float32 is expected - exactly what the capture gives.</summary>
    Task<ASRResult> Transcribe(float[] samples);

    /// <summary>
    /// Recognize with a language hint.
    ///
    /// <paramref name="languageHint"/> - BCP-47 code ("en", "ru"). <c>null</c> - autodetection by
    /// sound. The hint narrows the recognition to one language: this is the way out for
    /// cases when the accent leads the autodetection to the wrong place, but it breaks mixed
    /// speech - that is why the default is always <c>null</c>.
    ///
    /// The hint is an optional ability of the engine: an engine that does not understand it
    /// recognizes as usual. This is a hint contract, not a degradation.
    /// </summary>
    Task<ASRResult> Transcribe(float[] samples, string? languageHint) => Transcribe(samples);

    /// <summary>Free up the memory under the model.</summary>
    Task Unload();
}
