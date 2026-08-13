using System.Text;

namespace DictationCore;

/// <summary>
/// Deterministic refinement of recognized text.
///
/// No language models: only what can be tested and predicted.
/// Edits here do not change the meaning - they remove traces of the fact that the text came from
/// recognition, and was not typed by hand.
///
/// We do NOT consciously do this: we do not expand the numerals ("twenty-five" → "25")
/// - in Russian it depends on declensions and cases and breaks more than it fixes.
/// </summary>
public static class TranscriptPolisher
{
    /// <summary>
    /// Transform the recognized text into a form suitable for insertion.
    ///
    /// Protected spans (<see cref="ProtectedSpan"/>) are not affected by any operation.
    /// Spans are recalculated before each step, rather than being counted once:
    /// the previous step could have shifted the offsets, and the "floating" range is exactly that
    /// class of error due to which such systems rot. Detection is clean and
    /// idempotent, so recalculation costs nothing and changes nothing.
    /// </summary>
    public static string Polish(string text)
    {
        string result = text.Trim();
        if (result.Length == 0) { return ""; }

        result = CollapseWhitespace(result, Spans(result));
        result = FixSpacingAroundPunctuation(result, Spans(result));
        result = CapitalizeFirstLetter(result, Spans(result));
        return result;
    }

    private static IReadOnlyList<ProtectedSpan> Spans(string text) =>
        ProtectedSpanDetector.Detect(text);

    /// <summary>Collapse duplicate spaces while preserving newlines.</summary>
    internal static string CollapseWhitespace(string text) =>
        CollapseWhitespace(text, []);

    /// <summary>
    /// The same, but the spaces inside the protected piece remain as is: in
    /// the backticks lies code, and the alignment in it is significant.
    /// </summary>
    internal static string CollapseWhitespace(string text, IReadOnlyList<ProtectedSpan> spans)
    {
        if (spans.Count == 0) { return CollapseWhitespaceEverywhere(text); }

        string[] characters = GraphemeText.Split(text);
        var result = new List<string>(characters.Length);
        int spanIndex = 0;
        int index = 0;

        while (index < characters.Length)
        {
            while (spanIndex < spans.Count && spans[spanIndex].Range.End.Value <= index)
            {
                spanIndex += 1;
            }
            if (spanIndex < spans.Count && Contains(spans[spanIndex].Range, index))
            {
                ProtectedSpan span = spans[spanIndex];
                result.AddRange(characters[span.Range]);
                index = span.Range.End.Value;
                continue;
            }

            string character = characters[index];
            if (character == " " || character == "\t")
            {
                // The space in front of the protected piece also collapses - it is not his.
                if (Last(result) != " " && Last(result) != "\n") { result.Add(" "); }
                index += 1;
                continue;
            }
            result.Add(character);
            index += 1;
        }

        return string.Concat(result);
    }

    /// <summary>
    /// The port of Swift's split/join pair: the lines keep their empties, the words
    /// inside a line do not - that is what collapses the runs and trims the edges.
    /// </summary>
    private static string CollapseWhitespaceEverywhere(string text)
    {
        var lines = new List<string>();
        var words = new List<string>();
        var word = new StringBuilder();

        void FinishWord()
        {
            if (word.Length != 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        void FinishLine()
        {
            FinishWord();
            lines.Add(string.Join(" ", words));
            words.Clear();
        }

        foreach (string character in GraphemeText.Split(text))
        {
            if (character == "\n") { FinishLine(); continue; }
            if (character == " " || character == "\t") { FinishWord(); continue; }
            word.Append(character);
        }
        FinishLine();

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Remove the space before the punctuation mark and put it there after it,
    /// where it is definitely missing.
    ///
    /// Space insertion is deliberately careful. The model itself transforms
    /// "three and fourteen hundredths" in "3.14", and the period appears in versions
    /// ("2.0.1"), domains ("wai.computer") and abbreviations ("etc."); colon -
    /// in links ("https://") and time ("12:30"). Previously, a space was placed after
    /// any point, and everything listed fell apart - tested on the real
    /// model output.
    ///
    /// Therefore, the condition for a period and a colon remains the same: a space is placed
    /// only before a capital letter, that is, when a new sentence begins.
    ///
    /// The "space only before the capital" guard remains after the appearance of
    /// spans: it and the span model are filtered along different axes. The guard is watching
    /// what comes AFTER the sign (this is what <c>3.14</c>, <c>wai.computer</c>, <c>etc.</c> are protected by,
    /// <c>https://</c>, <c>12:30</c> - there is a lowercase or a number next), span looks at WHAT
    /// is a token. Removing the guards, since there are spans, is a tempting
    /// error: numbers, domains and abbreviations do not become spans and would be deprived
    /// of protection. Spans fix the only defect in the guard - <c>TextPipeline.Output</c>.
    /// </summary>
    internal static string FixSpacingAroundPunctuation(string text) =>
        FixSpacingAroundPunctuation(text, []);

    internal static string FixSpacingAroundPunctuation(
        string text,
        IReadOnlyList<ProtectedSpan> spans)
    {
        string[] characters = GraphemeText.Split(text);
        var result = new List<string>(characters.Length);
        int spanIndex = 0;
        int index = 0;
        while (index < characters.Length)
        {
            // The protected piece is carried verbatim: this is where `Foo.Bar`
            // stops turning into `Foo. Bar`.
            while (spanIndex < spans.Count && spans[spanIndex].Range.End.Value <= index)
            {
                spanIndex += 1;
            }
            if (spanIndex < spans.Count && Contains(spans[spanIndex].Range, index))
            {
                ProtectedSpan span = spans[spanIndex];
                result.AddRange(characters[span.Range]);
                index = span.Range.End.Value;
                continue;
            }

            string character = characters[index];

            if (Closing.Contains(character))
            {
                // The space before the punctuation mark is a recognition artifact.
                while (Last(result) == " ") { result.RemoveAt(result.Count - 1); }
                result.Add(character);

                string? next = index + 1 < characters.Length ? characters[index + 1] : null;
                if (next is not null
                    && (IsUppercase(next) || (AlwaysSeparating.Contains(character) && IsLetter(next))))
                {
                    result.Add(" ");
                }
                index += 1;
                continue;
            }

            result.Add(character);
            index += 1;
        }

        return string.Concat(result);
    }

    private static readonly HashSet<string> Closing =
        new(new[] { ",", ".", "!", "?", ";", ":", "…" }, StringComparer.Ordinal);

    /// <summary>
    /// Characters after which a space is always needed if there is a letter next.
    ///
    /// "first, second" the model always gives, and there is no capital letter there -
    /// with the condition "only before the capital" the enumeration remained
    /// stuck together. These signs do not have a single spelling where the letter is
    /// right to the point: the only such place at the comma is the decimal
    /// fraction, and then there is a number, and the rule does not touch it.
    /// </summary>
    private static readonly HashSet<string> AlwaysSeparating =
        new(new[] { ",", "!", "?", ";", "…" }, StringComparer.Ordinal);

    /// <summary>Capitalize the first letter - recognition often gives a lowercase one.</summary>
    internal static string CapitalizeFirstLetter(string text) =>
        CapitalizeFirstLetter(text, []);

    /// <summary>
    /// The identifier at the beginning of the dictation does not receive a capitalization: the dictated
    /// first word <c>onTextInserted</c> is a name, not the beginning of a sentence, and
    /// <c>OnTextInserted</c> would just be a different name.
    /// </summary>
    internal static string CapitalizeFirstLetter(string text, IReadOnlyList<ProtectedSpan> spans)
    {
        string[] characters = GraphemeText.Split(text);
        if (characters.Length == 0 || !IsLowercase(characters[0])) { return text; }
        if (spans.Any(span => Contains(span.Range, 0))) { return text; }
        return characters[0].ToUpperInvariant() + string.Concat(characters[1..]);
    }

    // MARK: - Grapheme-cluster helpers
    //
    // The unit everywhere here is the grapheme cluster (Swift's `Character`), never a
    // UTF-16 `char`: the offsets of `ProtectedSpan` count exactly those.

    /// <summary>The port of Swift's <c>Range.contains</c> over grapheme-cluster offsets.</summary>
    private static bool Contains(Range range, int index) =>
        index >= range.Start.Value && index < range.End.Value;

    /// <summary>The port of Swift's <c>String.last</c>: the trailing grapheme cluster.</summary>
    private static string? Last(List<string> characters) =>
        characters.Count == 0 ? null : characters[^1];

    private static Rune FirstRune(string character) =>
        Rune.TryGetRuneAt(character, 0, out var rune) ? rune : default;

    private static bool IsLetter(string character) => Rune.IsLetter(FirstRune(character));

    private static bool IsUppercase(string character) => Rune.IsUpper(FirstRune(character));

    private static bool IsLowercase(string character) => Rune.IsLower(FirstRune(character));
}
