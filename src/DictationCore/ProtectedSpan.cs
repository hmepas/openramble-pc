using System.Globalization;
using System.Text;

namespace DictationCore;

/// <summary>
/// What exactly is the protected piece.
///
/// The set is closed. Each case is not "similar to code", but a specific
/// spelling that a person dictates and expects to see verbatim.
/// </summary>
/// <remarks>
/// The Swift original nests this as <c>ProtectedSpan.Kind</c>; C# forbids a nested
/// type and a property of the same name, so it lives next to the span instead.
/// </remarks>
public enum ProtectedSpanKind
{
    /// <summary>The text in backquotes along with the quotes themselves.</summary>
    Backticks,

    /// <summary>File system path or URL.</summary>
    Path,

    /// <summary>Command line switch: <c>--dry-run</c>, <c>-x</c>, <c>--out=build/app</c>.</summary>
    Flag,

    /// <summary>CamelCase or dotted identifier: <c>TextPipeline</c>, <c>AppState.shared</c>.</summary>
    Identifier,
}

/// <summary>
/// A piece of text that, after the dictionary, is not touched by any stage of the pipeline.
///
/// Appeared for a specific defect: the polisher puts a space after the dot before
/// a capital letter, and <c>TextPipeline.Output</c> turned into <c>TextPipeline. Output</c>.
/// The same mechanism covers a more unpleasant one - a phonetic replacement
/// triggered inside a path or backticks.
///
/// The range is stored in character offsets (grapheme clusters, Swift's <c>Character</c>),
/// not in UTF-16 code units and not in byte offsets: the entire pipeline already moves along
/// the grapheme array, and translating between index kinds has already caused a bug once
/// in this repository.
/// </summary>
public sealed record ProtectedSpan
{
    public ProtectedSpan(ProtectedSpanKind kind, Range range, string text)
    {
        Kind = kind;
        Range = range;
        Text = text;
    }

    public ProtectedSpanKind Kind { get; }

    /// <summary>
    /// Character offsets in the text for which the span is calculated.
    /// The unit is the grapheme cluster (a text element), never a UTF-16 <c>char</c>.
    /// </summary>
    public Range Range { get; }

    /// <summary>
    /// The span text itself.
    ///
    /// Duplicates <see cref="Range"/> intentionally: the "spans have not changed" gate becomes
    /// a comparison of strings, and a failed test is read without recalculating the offsets in the head.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Search for protected spans in the text.
///
/// Pure function: the same text always gives the same spans. The
/// entire pipeline contract rests on this - the stages recalculate the spans after each
/// text edit, so in principle there are no "floating" offsets.
///
/// The scan goes from left to right, the spans do not intersect, and in a conflict the one
/// that started earlier and is longer wins.
/// </summary>
internal static class ProtectedSpanDetector
{
    internal static IReadOnlyList<ProtectedSpan> Detect(string text)
    {
        string[] characters = GraphemeText.Split(text);
        var spans = new List<ProtectedSpan>();
        int index = 0;

        while (index < characters.Length)
        {
            // Backticks are first and unconditional: there is already code inside, and there
            // is nothing to look for with the remaining rules there.
            if (characters[index] == "`" && BacktickSpan(characters, index) is { } fenced)
            {
                spans.Add(fenced);
                index = fenced.Range.End.Value;
                continue;
            }

            if (!IsTokenStart(characters, index))
            {
                index += 1;
                continue;
            }

            int end = TokenEnd(characters, index);

            // A backtick that opened not the token, but its middle: `"`a  b`"`
            // begins with a quotation mark. Jumping to the end of the token
            // carried the scan over the pair entirely - the polisher then collapsed
            // the spaces inside, and phonetic matching worked in a bracketed
            // piece of code. We rewind to the backtick itself; it is always ahead
            // of `index`, so the cycle continues to move forward.
            int backtick = FirstIndexOf(characters, "`", index, end);
            if (backtick > index)
            {
                index = backtick;
                continue;
            }

            string[] token = characters[index..end];

            if (SpanForToken(token, index) is { } span)
            {
                spans.Add(span);
            }

            index = end;
        }

        return spans;
    }

    // MARK: - Backticks

    /// <summary>A pair of the same number of backticks with non-empty content without a line break.</summary>
    private static ProtectedSpan? BacktickSpan(string[] characters, int start)
    {
        int openEnd = start;
        while (openEnd < characters.Length && characters[openEnd] == "`") { openEnd += 1; }
        int fenceLength = openEnd - start;

        int cursor = openEnd;
        while (cursor < characters.Length)
        {
            if (characters[cursor] == "\n") { return null; }
            if (characters[cursor] != "`") { cursor += 1; continue; }

            int closeEnd = cursor;
            while (closeEnd < characters.Length && characters[closeEnd] == "`") { closeEnd += 1; }
            if (closeEnd - cursor != fenceLength) { cursor = closeEnd; continue; }
            if (cursor <= openEnd) { return null; }

            var range = new Range(start, closeEnd);
            return new ProtectedSpan(
                kind: ProtectedSpanKind.Backticks,
                range: range,
                text: string.Concat(characters[range]));
        }

        return null;
    }

    // MARK: - Tokens

    /// <summary>The token begins where the whitespace ended, and only with "non-whitespace".</summary>
    private static bool IsTokenStart(string[] characters, int index)
    {
        if (IsWhitespace(characters[index])) { return false; }
        if (index == 0) { return true; }
        return IsWhitespace(characters[index - 1]);
    }

    private static int TokenEnd(string[] characters, int start)
    {
        int end = start;
        while (end < characters.Length && !IsWhitespace(characters[end])) { end += 1; }
        return end;
    }

    /// <summary>Parsing one token. The order of checks is from the most specific to the most general.</summary>
    private static ProtectedSpan? SpanForToken(string[] token, int start)
    {
        // Punctuation on both sides belongs to the phrase, not to the token:
        // "open /etc/hosts.", «TextPipeline.Output», [AppState.shared].
        int leading = 0;
        while (leading < token.Length && IsLeadingPunctuation(token[leading])) { leading += 1; }
        int upper = token.Length;
        while (upper > leading && IsTrailingPunctuation(token[upper - 1])) { upper -= 1; }
        if (upper == leading) { return null; }

        string[] core = token[leading..upper];
        ProtectedSpanKind? kind;
        if (IsFlag(core))
        {
            kind = ProtectedSpanKind.Flag;
        }
        else if (IsPath(core))
        {
            kind = ProtectedSpanKind.Path;
        }
        else if (IsIdentifier(core))
        {
            kind = ProtectedSpanKind.Identifier;
        }
        else
        {
            kind = null;
        }

        if (kind is not { } resolved) { return null; }

        // The slice keeps the indices of the token, so its beginning is exactly
        // how much was cut off on the left.
        int lower = start + leading;
        return new ProtectedSpan(
            kind: resolved,
            range: new Range(lower, lower + core.Length),
            text: string.Concat(core));
    }

    /// <summary>
    /// An opening bracket or quotation mark.
    ///
    /// « » is the standard Russian quotation mark, the model emits it constantly.
    /// Until the beginning was cut off, one such character made the whole token
    /// invisible to the detector - and every stage after the dictionary rewrote
    /// the name inside it.
    ///
    /// Cutting does not make speech a span: every rule below requires either the
    /// Latin alphabet or a slash at the beginning of the path, and a Cyrillic word
    /// in quotation marks or brackets remains just a word.
    /// </summary>
    private static bool IsLeadingPunctuation(string character) =>
        "«\"'([{“‘".Contains(character, StringComparison.Ordinal);

    private static bool IsTrailingPunctuation(string character) =>
        ",.!?;:…»\"')]}”’".Contains(character, StringComparison.Ordinal);

    // MARK: - Rules

    /// <summary>
    /// <c>--flag</c>, <c>--flag=value</c> or a short <c>-x</c> of exactly one letter.
    ///
    /// One letter is not a quibble: <c>-5</c> is a number, and <c>-xvf</c> does not exist in dictation,
    /// but "word-word" does happen, and you cannot turn it into a flag.
    /// </summary>
    private static bool IsFlag(string[] token)
    {
        if (token.Length >= 3 && token[0] == "-" && token[1] == "-")
        {
            return IsLetter(token[2]) && IsASCII(token[2]);
        }

        if (token.Length == 2 && token[0] == "-")
        {
            return IsLetter(token[1]) && IsASCII(token[1]);
        }

        return false;
    }

    /// <summary>
    /// Path: a slash and one of three confirmations are required.
    ///
    /// A bare <c>foo/bar</c> is not enough - in Russian speech it is "or", and such combinations
    /// are noticeably more frequent than paths of two simple segments.
    /// </summary>
    private static bool IsPath(string[] token)
    {
        if (!Contains(token, "/")) { return false; }

        if (ContainsSequence(token, ":", "/", "/")) { return true; }
        if (HasPrefix(token, "/") || HasPrefix(token, ".", "/")
            || HasPrefix(token, ".", ".", "/") || HasPrefix(token, "~", "/"))
        {
            return HasNonEmptySegment(token);
        }

        var segments = SplitKeepingEmpty(token, "/");
        if (segments.Count < 2 || segments.Any(segment => segment.Count == 0)) { return false; }
        if (!segments.All(segment => segment.All(IsPathSegmentCharacter))) { return false; }

        int slashes = token.Count(character => character == "/");
        bool hasRichSegment = segments.Any(segment =>
            segment.Contains(".") || segment.Contains("_") || segment.Contains("-"));
        return slashes >= 2 || hasRichSegment;
    }

    private static bool HasNonEmptySegment(string[] token) =>
        SplitOmittingEmpty(token, "/").Any(segment => segment.Count != 0);

    private static bool IsPathSegmentCharacter(string character)
    {
        if (!IsASCII(character)) { return false; }
        if (IsLetter(character) || IsNumber(character)) { return true; }
        return "._+@-".Contains(character, StringComparison.Ordinal);
    }

    /// <summary>CamelCase or dotted identifier with a capital in at least one segment.</summary>
    private static bool IsIdentifier(string[] token)
    {
        if (!token.All(IsASCII)) { return false; }

        if (Contains(token, "."))
        {
            var segments = SplitKeepingEmpty(token, ".");
            if (segments.Count < 2 || segments.Any(segment => segment.Count == 0)) { return false; }
            if (!segments.All(IsIdentifierSegment)) { return false; }
            // Domains and versions are cut off here: `wai.computer` and `2.0.1` have
            // neither a capital nor CamelCase - and both are already protected by the polisher guard.
            return segments.Any(segment => IsUppercase(segment[0]) || IsCamelCase(segment));
        }

        return IsCamelCase(token);
    }

    private static bool IsIdentifierSegment(IReadOnlyList<string> segment)
    {
        if (segment.Count == 0) { return false; }
        string first = segment[0];
        if (!(IsLetter(first) || first == "_")) { return false; }
        return segment.All(character => IsLetter(character) || IsNumber(character) || character == "_");
    }

    /// <summary>
    /// Two CamelCase forms, both real.
    ///
    /// The first is the transition from lowercase to uppercase: <c>onTextInserted</c>, <c>TextPipeline</c>.
    /// The second is an abbreviation followed by a word: <c>URLSession</c>,
    /// <c>NSString</c>, <c>HTTPServer</c>. In the second there is not a single transition lower→upper,
    /// so without a separate rule such names would remain unprotected.
    ///
    /// <c>API</c>, <c>JSON</c>, <c>HTTP</c> fit neither of them: nothing starts after
    /// the abbreviation. They do not need protection - there is no point inside, there is nothing to break.
    ///
    /// Protecting a single word is not a formality: without it capitalization of the first letter
    /// turns the dictated first word <c>onTextInserted</c> into <c>OnTextInserted</c>.
    /// </summary>
    private static bool IsCamelCase(IReadOnlyList<string> token)
    {
        if (!token.All(character => IsLetter(character) || IsNumber(character))) { return false; }

        for (int index = 0; index + 1 < token.Count; index += 1)
        {
            if (IsLowercase(token[index]) && IsUppercase(token[index + 1])) { return true; }
        }

        for (int index = 0; index < Math.Max(0, token.Count - 2); index += 1)
        {
            if (IsUppercase(token[index])
                && IsUppercase(token[index + 1])
                && IsLowercase(token[index + 2]))
            {
                return true;
            }
        }

        return false;
    }

    // MARK: - Grapheme-cluster helpers
    //
    // Every predicate below takes one grapheme cluster (Swift's `Character`) and
    // answers about its first scalar, the way Swift's `Character` properties do.

    private static Rune FirstRune(string character) =>
        Rune.TryGetRuneAt(character, 0, out var rune) ? rune : default;

    private static bool IsWhitespace(string character) => Rune.IsWhiteSpace(FirstRune(character));

    private static bool IsLetter(string character) => Rune.IsLetter(FirstRune(character));

    private static bool IsNumber(string character) => Rune.IsNumber(FirstRune(character));

    private static bool IsUppercase(string character) => Rune.IsUpper(FirstRune(character));

    private static bool IsLowercase(string character) => Rune.IsLower(FirstRune(character));

    /// <summary>A single ASCII scalar, exactly like Swift's <c>Character.isASCII</c>.</summary>
    private static bool IsASCII(string character) => character.Length == 1 && character[0] < 128;

    private static int FirstIndexOf(string[] characters, string needle, int from, int to)
    {
        for (int index = from; index < to; index += 1)
        {
            if (characters[index] == needle) { return index; }
        }

        return -1;
    }

    private static bool Contains(string[] characters, string needle) =>
        Array.IndexOf(characters, needle) >= 0;

    private static bool ContainsSequence(string[] characters, params string[] needle)
    {
        for (int index = 0; index + needle.Length <= characters.Length; index += 1)
        {
            if (characters.Skip(index).Take(needle.Length).SequenceEqual(needle)) { return true; }
        }

        return false;
    }

    private static bool HasPrefix(string[] characters, params string[] prefix)
    {
        if (characters.Length < prefix.Length) { return false; }
        return characters.Take(prefix.Length).SequenceEqual(prefix);
    }

    private static List<List<string>> SplitKeepingEmpty(string[] characters, string separator)
    {
        var segments = new List<List<string>> { new() };
        foreach (string character in characters)
        {
            if (character == separator)
            {
                segments.Add([]);
            }
            else
            {
                segments[^1].Add(character);
            }
        }

        return segments;
    }

    private static List<List<string>> SplitOmittingEmpty(string[] characters, string separator) =>
        SplitKeepingEmpty(characters, separator).Where(segment => segment.Count != 0).ToList();
}

/// <summary>
/// The unit the whole pipeline counts in: the grapheme cluster, Swift's <c>Character</c>.
/// </summary>
internal static class GraphemeText
{
    /// <summary>The port of Swift's <c>Array(text)</c>: one string per grapheme cluster.</summary>
    internal static string[] Split(string text)
    {
        var characters = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            characters.Add((string)enumerator.Current);
        }

        return characters.ToArray();
    }

    /// <summary>The port of Swift's <c>String.count</c>: grapheme clusters, not UTF-16 units.</summary>
    internal static int Count(string text) => new StringInfo(text).LengthInTextElements;
}
