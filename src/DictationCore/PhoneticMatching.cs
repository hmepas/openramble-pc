using System.Text;
using System.Text.RegularExpressions;

namespace DictationCore;

/// <summary>
/// Phonetic key of the Russian word.
///
/// Exists because a flat spelling table does not work in principle:
/// the spelling of the term depends on the phrase in which it was used. One word
/// "deploy", said in one voice, the model writes as "диплой", "депла",
/// "деплей", "дисплей" - and the next phrase will have a fifth spelling. The key
/// collapses exactly those differences that Russian phonetics does not distinguish anyway,
/// and leaves everything else.
///
/// What is collapsed:
///
/// <list type="bullet">
/// <item><description><b>unstressed vowels</b>: "о" sounds like "а", "е"/"э"/"и"/"ы" merge,
/// "ю" is like "у". Hence "диплой" = "деплой", "бэкэнд" = "бэкенд",
/// "камит" = "комит", "мордж" = "мёрдж";</description></item>
/// <item><description><b>paired consonants in voicing - only where Russian really does not
/// distinguish them</b>: at the end of a word and before another obstruent. Hence "ребейс" =
/// "ребейз" and "фронтент" = "фронтенд";</description></item>
/// <item><description><b>doubling</b>: "комит" = "коммит", "риллис" = "рилис".</description></item>
/// </list>
///
/// What does NOT collapse, and this is the main thing:
///
/// <list type="bullet">
/// <item><description><b>the last letter, if it is a vowel.</b> The case ending lives in it, and
/// to collapse it means to declare "центре" and "центри" one word - that is,
/// to turn "в центре города" into "в Sentry города";</description></item>
/// <item><description><b>voicing at the beginning of a word and between vowels.</b> Russian
/// devoices nothing there, and collapsing turns "тёплой" into "деплой", "бетон" and
/// "бидон" into "питон", "протёкш" into "продакш". The measurement was made on the same
/// set: with the collapse, 161 real Russian words fall under replacement,
/// with the positional one - 70, and the winnings are exactly the same;</description></item>
/// <item><description><b>the soft and hard sign.</b> "Камедь" ≠ "камет", "ревю" ≠ "ревью".</description></item>
/// </list>
///
/// The damage radius is measured by brute force, not by reasoning: for each entry
/// all spellings with the same key are built, and the system spell checker
/// selects real Russian words from them. The supplied set still has 31 of them,
/// and every single one is the terms themselves, which have taken root in Russian ("релиз", "билд",
/// "пиар", "ревью", "продакшн"). There is no ordinary speech among them.
/// </summary>
/// <remarks>
/// Every table below is keyed by a grapheme cluster (Swift's <c>Character</c>), never by a
/// UTF-16 <c>char</c>: the key is built from the same units the rest of the pipeline counts in.
/// </remarks>
internal static class PhoneticKey
{
    /// <summary>
    /// Vowels and what they collapse into.
    ///
    /// "я" is left to itself: its reduction depends on the softness of its neighbor, and without
    /// stresses it is impossible to guess.
    /// </summary>
    private static readonly Dictionary<string, string> Vowels = new()
    {
        ["а"] = "а", ["о"] = "а", ["ё"] = "а",
        ["е"] = "е", ["и"] = "е", ["э"] = "е", ["ы"] = "е",
        ["у"] = "у", ["ю"] = "у",
        ["я"] = "я",
    };

    /// <summary>Paired by voicing - to the voiceless one.</summary>
    private static readonly Dictionary<string, string> Paired = new()
    {
        ["б"] = "п", ["п"] = "п",
        ["в"] = "ф", ["ф"] = "ф",
        ["г"] = "к", ["к"] = "к",
        ["д"] = "т", ["т"] = "т",
        ["ж"] = "ш", ["ш"] = "ш",
        ["з"] = "с", ["с"] = "с",
    };

    /// <summary>Obstruents: before them, assimilation in terms of voicing occurs.</summary>
    private static readonly HashSet<string> Obstruents =
    [
        "б", "п", "в", "ф", "г", "к", "д", "т", "ж", "ш", "з", "с", "х", "ц", "ч", "щ",
    ];

    internal static string Of(string word)
    {
        string[] lowered = GraphemeText.Split(word.ToLowerInvariant());
        var squeezed = new List<string>();
        foreach (string character in lowered)
        {
            if (squeezed.Count == 0 || squeezed[^1] != character) { squeezed.Add(character); }
        }

        var key = new StringBuilder(squeezed.Count);
        for (int offset = 0; offset < squeezed.Count; offset += 1)
        {
            string character = squeezed[offset];
            bool isLast = offset == squeezed.Count - 1;

            if (Vowels.TryGetValue(character, out string? folded))
            {
                key.Append(isLast ? character : folded);
                continue;
            }

            if (Paired.TryGetValue(character, out string? devoiced))
            {
                bool neutralised = isLast || Obstruents.Contains(squeezed[offset + 1]);
                if (neutralised)
                {
                    key.Append(devoiced);
                    continue;
                }
            }

            key.Append(character);
        }

        return key.ToString();
    }
}

/// <summary>
/// Second pass of the dictionary: a term written differently than in the dictionary.
///
/// Works only on Cyrillic words and only on those which the exact
/// match did not reach: the Latin alphabet by this moment has already been inserted and is not
/// subject to parsing.
/// </summary>
internal static class PhoneticMatching
{
    /// <summary>
    /// We do not take a phonetic key shorter than five letters.
    ///
    /// Short words have too much ordinary speech per key:
    /// "апи", up to unstressed vowels, coincides with "эпи", "опа", "упа".
    /// Five is not a round number, but a limit below which the measurement began
    /// to run into ordinary words.
    /// </summary>
    internal const int MinimumLetters = 5;

    /// <summary>
    /// How many words in a row one term can occupy.
    ///
    /// The model either glues the term into one word, or breaks it into two: "даун
    /// тайм" and "даунтайм", "джава скрипт" and "джаваскрипт", "энд поинт" and
    /// "эндпоинт". The window closes both entries with one check.
    /// </summary>
    private const int MaximumWindow = 3;

    private sealed class Index
    {
        /// <summary>Record key → how to write.</summary>
        internal Dictionary<string, string> Exact { get; } = [];

        /// <summary>The same, but only for records that are inflected.</summary>
        internal Dictionary<string, string> Inflected { get; } = [];
    }

    /// <summary>
    /// A half-open range of grapheme clusters (Swift's <c>Character</c>), never of UTF-16 units.
    /// </summary>
    private readonly record struct GraphemeRange(int Lower, int Upper)
    {
        internal bool IsEmpty => Lower >= Upper;

        internal bool Contains(int position) => position >= Lower && position < Upper;

        /// <summary>The port of Swift's <c>Range.overlaps</c>: empty ranges overlap nothing.</summary>
        internal bool Overlaps(GraphemeRange other) =>
            (!other.IsEmpty && Contains(other.Lower)) || (!IsEmpty && other.Contains(Lower));
    }

    internal static string Apply(IReadOnlyList<DictionaryReplacement> replacements, string text) =>
        Apply(replacements, text, []);

    /// <summary>
    /// Phonetic selection that bypasses protected spans.
    ///
    /// Two separate protections, both needed. The first is that a word inside a span does not
    /// get into the list of candidates. The second is that a three-word window has no right
    /// to step over a span: otherwise "сентри" to the left and "сентри" to the right of a path
    /// would be glued together into one term through a protected piece.
    ///
    /// The empty set of spans goes through exactly the same code as before: the filter
    /// becomes identical, there are no breaks, the window limit does not change.
    /// </summary>
    internal static string Apply(
        IReadOnlyList<DictionaryReplacement> replacements,
        string text,
        IReadOnlyList<ProtectedSpan> spans)
    {
        Index index = MakeIndex(replacements);
        if (index.Exact.Count == 0 || text.Length == 0) { return text; }

        string[] characters = GraphemeText.Split(text);
        List<GraphemeRange> protectedRanges = IndexRanges(spans, characters.Length);
        List<GraphemeRange> words = WordRanges(text, characters)
            .Where(word => !protectedRanges.Any(range => range.Overlaps(word)))
            .ToList();
        if (words.Count == 0) { return text; }

        // Before which word the window must break: between it and the previous one
        // there is a protected piece.
        var breaksBefore = new HashSet<int>();
        if (protectedRanges.Count != 0)
        {
            for (int position = 1; position < words.Count; position += 1)
            {
                var gap = new GraphemeRange(words[position - 1].Upper, words[position].Lower);
                if (protectedRanges.Any(range => range.Overlaps(gap)))
                {
                    breaksBefore.Add(position);
                }
            }
        }

        var result = new StringBuilder();
        int cursor = 0;
        int start = 0;

        while (start < words.Count)
        {
            bool matched = false;
            int size = Math.Min(MaximumWindow, words.Count - start);
            // Shorten the window to the nearest break.
            for (int offset = 1; offset < Math.Max(size, 1); offset += 1)
            {
                if (breaksBefore.Contains(start + offset))
                {
                    size = offset;
                    break;
                }
            }

            while (size >= 1)
            {
                List<GraphemeRange> window = words.GetRange(start, size);
                if (Lookup(window, characters, index) is { } written)
                {
                    result.Append(string.Concat(characters[cursor..window[0].Lower]));
                    result.Append(written);
                    cursor = window[size - 1].Upper;
                    start += size;
                    matched = true;
                    break;
                }

                size -= 1;
            }

            if (!matched) { start += 1; }
        }

        result.Append(string.Concat(characters[cursor..]));
        return result.ToString();
    }

    /// <summary>Character offsets of spans → ranges of the line to which they are calculated.</summary>
    private static List<GraphemeRange> IndexRanges(IReadOnlyList<ProtectedSpan> spans, int count)
    {
        if (spans.Count == 0) { return []; }
        return spans
            .Where(span => span.Range.Start.Value >= 0 && span.Range.End.Value <= count)
            .Select(span => new GraphemeRange(span.Range.Start.Value, span.Range.End.Value))
            .ToList();
    }

    // MARK: - What to look for

    private static Index MakeIndex(IReadOnlyList<DictionaryReplacement> replacements)
    {
        var index = new Index();
        var ambiguous = new HashSet<string>();
        var ambiguousInflected = new HashSet<string>();

        foreach (DictionaryReplacement replacement in replacements)
        {
            if (!replacement.AllowsPhoneticMatching) { continue; }
            string spoken = DictionaryWhitespace.Trimmed(replacement.Spoken);
            // The empty right side is a way to cross out a filler word. An exact
            // match is capable of this, phonetic selection is not: a deletion is not
            // visible in the text, and a person will not notice the mistake here at all.
            if (replacement.Written.Length == 0) { continue; }

            string letters = Compacted(spoken);
            if (GraphemeText.Count(letters) < MinimumLetters || !IsCyrillic(letters)) { continue; }

            string key = PhoneticKey.Of(letters);
            if (index.Exact.TryGetValue(key, out string? existing) && existing != replacement.Written)
            {
                ambiguous.Add(key);
            }

            index.Exact[key] = replacement.Written;

            if (!replacement.Inflects) { continue; }
            if (index.Inflected.TryGetValue(key, out string? existingInflected)
                && existingInflected != replacement.Written)
            {
                ambiguousInflected.Add(key);
            }

            index.Inflected[key] = replacement.Written;
        }

        // One key for two different terms is not a choice, but a guess.
        foreach (string key in ambiguous) { index.Exact.Remove(key); }
        foreach (string key in ambiguousInflected) { index.Inflected.Remove(key); }
        return index;
    }

    // MARK: - Text parsing

    /// <summary>Words of the text. A digit next to the letters makes the word different: "апи2" is not "апи".</summary>
    private static readonly Regex WordExpression = new(
        "(?<![\\p{L}\\p{N}])\\p{L}+(?:-\\p{L}+)*(?![\\p{L}\\p{N}])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Word ranges in grapheme clusters.
    /// </summary>
    /// <remarks>
    /// The regular expression runs over UTF-16, so its offsets are translated back into the
    /// unit everything else counts in. A match whose edge falls inside a grapheme cluster is
    /// dropped, exactly as Swift's <c>Range(_:in:)</c> drops an NSRange that is not on a
    /// <c>Character</c> boundary.
    /// </remarks>
    private static List<GraphemeRange> WordRanges(string text, string[] characters)
    {
        int[] offsets = GraphemeOffsets(text, characters);
        var ranges = new List<GraphemeRange>();
        foreach (Match match in WordExpression.Matches(text))
        {
            int lower = offsets[match.Index];
            int upper = offsets[match.Index + match.Length];
            if (lower < 0 || upper < 0) { continue; }
            ranges.Add(new GraphemeRange(lower, upper));
        }

        return ranges;
    }

    /// <summary>UTF-16 offset → grapheme-cluster offset; <c>-1</c> for a position inside a cluster.</summary>
    private static int[] GraphemeOffsets(string text, string[] characters)
    {
        int[] offsets = new int[text.Length + 1];
        Array.Fill(offsets, -1);
        int position = 0;
        for (int index = 0; index < characters.Length; index += 1)
        {
            offsets[position] = index;
            position += characters[index].Length;
        }

        offsets[text.Length] = characters.Length;
        return offsets;
    }

    /// <summary>How to write these words if they are actually a term.</summary>
    private static string? Lookup(List<GraphemeRange> window, string[] characters, Index index)
    {
        // The words of the window must be in a row separated by spaces: "энд, поинт" are two
        // different places of the phrase, not a broken term.
        for (int position = 0; position + 1 < window.Count; position += 1)
        {
            string[] gap = characters[window[position].Upper..window[position + 1].Lower];
            if (gap.Length == 0 || !gap.All(character => character == " ")) { return null; }
        }

        List<string> parts = window
            .Select(word => Compacted(string.Concat(characters[word.Lower..word.Upper])))
            .ToList();
        if (!parts.All(part => IsCyrillic(part) && part.Length != 0)) { return null; }

        string head = string.Concat(parts.Take(parts.Count - 1));
        string tail = parts[^1];

        string whole = head + tail;
        if (GraphemeText.Count(whole) >= MinimumLetters
            && index.Exact.TryGetValue(PhoneticKey.Of(whole), out string? written))
        {
            return written;
        }

        foreach (string stem in Stems(tail))
        {
            string candidate = head + stem;
            if (GraphemeText.Count(candidate) < MinimumLetters) { continue; }
            if (index.Inflected.TryGetValue(PhoneticKey.Of(candidate), out string? inflected))
            {
                return inflected;
            }
        }

        return null;
    }

    /// <summary>
    /// The stems of the word after cutting off the case tail.
    ///
    /// The list of tails is the same closed one as that of an exact match, and the "й"
    /// returns to its place for the same reason: "деплою" is "депло" + "ю",
    /// and the term itself ends in "й".
    /// </summary>
    private static List<string> Stems(string word)
    {
        var found = new List<string>();
        foreach (string ending in InflectionEndings)
        {
            if (!word.EndsWith(ending, StringComparison.Ordinal)) { continue; }
            string stem = string.Concat(GraphemeText.Split(word)[..^GraphemeText.Count(ending)]);
            if (GraphemeText.Count(stem) < 3) { continue; }
            found.Add(stem);
            found.Add(stem + "й");
        }

        return found;
    }

    private static readonly string[] InflectionEndings =
    [
        "ами", "ями", "ой", "ей", "ом", "ем", "ов", "ев", "ам", "ям", "ах", "ях",
        "а", "е", "и", "ы", "у", "ю", "я",
    ];

    // MARK: - Little things

    /// <summary>A word without spaces or hyphens: they are optional for the term.</summary>
    private static string Compacted(string text) =>
        string.Concat(GraphemeText.Split(text)
            .Where(character => character != " " && character != "-"))
            .ToLowerInvariant();

    /// <summary>Latin substitutions are not subject to phonetic analysis: the rules are Russian.</summary>
    private static bool IsCyrillic(string text) =>
        text.Length != 0 && text.EnumerateRunes().All(rune => rune.Value is >= 0x0400 and <= 0x04FF);
}
