using System.Text;

namespace DictationCore;

/// <summary>
/// Convolution of typography to the writing that a person would type by hand.
///
/// Why is the stage needed now, before any language model:
///
/// 1. Applications with "smart" substitution (Notion, Pages, Mail) rewrite the
/// straight quotes in the field into double quotes, and a hyphen into a dash. <see cref="InsertedEditExtractor"/>
/// compares what was inserted with what was re-read, and after such substitution
/// the comparison ceases to converge - learning from edits silently dies.
/// 2. Invisible characters come through the dictionary: <c>written</c> a person inserts by
/// copy-paste, and a non-breaking space, BOM and soft hyphen are not visible in the settings
/// at all.
/// 3. Similar characters break the span model itself: <c>U+2212</c> MINUS before the letter
/// makes such a short switch a non-flag.
///
/// The table is closed and small on purpose. Everything that makes sense remains: guillemets are
/// correct Russian typography, a dash differs from a hyphen in meaning,
/// <see cref="TranscriptPolisher"/> owns the ellipsis.
/// </summary>
internal static class TypographyFold
{
    /// <summary>Exactly twelve characters. An empty string means "delete".</summary>
    internal static readonly IReadOnlyDictionary<string, string> Table =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Invisible spaces -> regular space.
            ["\u00A0"] = " ",   // NO-BREAK SPACE
            ["\u202F"] = " ",   // NARROW NO-BREAK SPACE
            ["\u2009"] = " ",   // THIN SPACE
            // Invisible at all -> delete.
            ["\u200B"] = "",    // ZERO WIDTH SPACE
            ["\uFEFF"] = "",    // ZERO WIDTH NO-BREAK SPACE / BOM
            ["\u00AD"] = "",    // SOFT HYPHEN
            // Paired quotes -> straight ones.
            ["\u2018"] = "'",   // LEFT SINGLE QUOTATION MARK
            ["\u2019"] = "'",   // RIGHT SINGLE QUOTATION MARK
            ["\u201C"] = "\"",  // LEFT DOUBLE QUOTATION MARK
            ["\u201D"] = "\"",  // RIGHT DOUBLE QUOTATION MARK
            // Similar to hyphen and dash -> canonical.
            ["\u2212"] = "-",   // MINUS SIGN
            ["\u2015"] = "\u2014",   // HORIZONTAL BAR -> EM DASH
        };

    internal static string Fold(string text) => Fold(text, []);

    /// <summary>
    /// Collapse everything except the protected pieces.
    ///
    /// There is code inside the backticks: there a quotation mark is significant and replacing it would break it.
    /// </summary>
    internal static string Fold(string text, IReadOnlyList<ProtectedSpan> spans)
    {
        if (text.Length == 0) { return text; }

        string[] characters = GraphemeText.Split(text);
        var result = new StringBuilder(characters.Length);

        int spanIndex = 0;
        int index = 0;
        while (index < characters.Length)
        {
            // The spans are sorted and do not intersect - one cursor is enough.
            while (spanIndex < spans.Count && spans[spanIndex].Range.End.Value <= index)
            {
                spanIndex += 1;
            }
            if (spanIndex < spans.Count && Contains(spans[spanIndex].Range, index))
            {
                ProtectedSpan span = spans[spanIndex];
                result.Append(string.Concat(characters[span.Range]));
                index = span.Range.End.Value;
                continue;
            }

            string character = characters[index];
            if (Table.TryGetValue(character, out string? replacement))
            {
                result.Append(replacement);
            }
            else
            {
                result.Append(character);
            }
            index += 1;
        }

        return result.ToString();
    }

    /// <summary>The port of Swift's <c>Range.contains</c> over grapheme-cluster offsets.</summary>
    private static bool Contains(Range range, int index) =>
        index >= range.Start.Value && index < range.End.Value;
}
