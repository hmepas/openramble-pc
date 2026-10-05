namespace DictationCore;

/// <summary>
/// The origin of the last dictation: what it became, and what it must not lose.
///
/// Two consumers. Correction learning needs <see cref="FinalText"/> and <see cref="Spans"/>: it
/// diffs what was inserted against what the person changed, and must not turn an edit inside a
/// path or backticks into a silent rule. And <see cref="AfterDictionary"/> is the checkpoint the
/// upstream conformance fixtures record, which is how the two pipelines are held to the same
/// behaviour at the dictionary stage.
///
/// The recognized text itself is deliberately not kept. It was here only for a "Copy Last as
/// Spoken" menu item, and carrying a verbatim copy of every dictation for a feature nobody
/// invoked is a cost with no reader.
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
        string afterDictionary,
        string finalText,
        IReadOnlyList<ProtectedSpan> spans)
    {
        AfterDictionary = afterDictionary;
        FinalText = finalText;
        Spans = spans;
    }

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
        && AfterDictionary == other.AfterDictionary
        && FinalText == other.FinalText
        && Spans.SequenceEqual(other.Spans);

    public override int GetHashCode() =>
        HashCode.Combine(AfterDictionary, FinalText, Spans.Count);

    /// <summary>Character counters only. The counted unit is the grapheme cluster.</summary>
    public override string ToString() =>
        $"PipelineProvenance(afterDictionary: {GraphemeText.Count(AfterDictionary)} chars, "
        + $"final: {GraphemeText.Count(FinalText)} chars, spans: {Spans.Count})";
}
