namespace DictationCore;

/// <summary>
/// The origin of the last dictation: what the text was and what it has become.
///
/// Two things are needed: "copy verbatim" - so that what was said is accessible even
/// after the dictionary and the polisher have worked on it - and diagnostics,
/// which separates "the dictionary did not work" from "it worked, but the cosmetics moved".
///
/// <b>Intentionally not serializable.</b> This is not forgetfulness, but the guarantee itself
/// "never to disk": to write such a structure to a file, one first has to consciously
/// add the conformance, and next to it is a test that fails on it.
///
/// <see cref="ToString"/> returns only field names and character counters - no
/// random interpolation into the log will bring the dictated text out.
/// </summary>
public sealed record PipelineProvenance
{
    public PipelineProvenance(
        string raw,
        string afterDictionary,
        string finalText,
        IReadOnlyList<ProtectedSpan> spans)
    {
        Raw = raw;
        AfterDictionary = afterDictionary;
        FinalText = finalText;
        Spans = spans;
    }

    /// <summary>Exactly what the recognition returned, before all stages.</summary>
    public string Raw { get; }

    /// <summary>The state after the dictionary entirely and before any cosmetics.</summary>
    public string AfterDictionary { get; }

    /// <summary>What will be inserted.</summary>
    public string FinalText { get; }

    /// <summary>
    /// Protected spans of the final text.
    ///
    /// They lie here because training on edits must bypass them, and
    /// it has no other honest source: it only sees the inserted text.
    /// </summary>
    public IReadOnlyList<ProtectedSpan> Spans { get; }

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(PipelineProvenance? other) =>
        other is not null
        && Raw == other.Raw
        && AfterDictionary == other.AfterDictionary
        && FinalText == other.FinalText
        && Spans.SequenceEqual(other.Spans);

    public override int GetHashCode() =>
        HashCode.Combine(Raw, AfterDictionary, FinalText, Spans.Count);

    /// <summary>Character counters only. The counted unit is the grapheme cluster.</summary>
    public override string ToString() =>
        $"PipelineProvenance(raw: {GraphemeText.Count(Raw)} chars, "
        + $"afterDictionary: {GraphemeText.Count(AfterDictionary)} chars,"
        + $"final: {GraphemeText.Count(FinalText)} chars, spans: {Spans.Count})";
}
