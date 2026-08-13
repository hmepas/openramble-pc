using System.Globalization;
using System.Text;

namespace AsrBench;

/// <summary>
/// Parsing text into words and punctuation marks.
///
/// Marks <b>are not thrown away</b>: they are split into separate tokens and counted
/// separately. This is fundamental - silently erasing punctuation means declaring it free, and
/// in dictation it is half the work.
/// </summary>
/// <param name="Words">Words without punctuation, numerals folded to digits.</param>
/// <param name="Tokens">Words and marks mixed, in the original order.</param>
/// <param name="Marks">Punctuation marks only, in the original order.</param>
internal sealed record NormalizedText(
    IReadOnlyList<string> Words,
    IReadOnlyList<string> Tokens,
    IReadOnlyList<string> Marks)
{
    public string Text => string.Join(" ", Words);

    /// <summary>
    /// The character string for CER: words separated by spaces, without marks.
    /// </summary>
    /// <remarks>
    /// Swift counts <c>Character</c>, that is grapheme clusters. Each element here is one
    /// grapheme cluster too - not a UTF-16 code unit - so a combining accent stays part of its
    /// letter instead of costing an extra edit.
    /// </remarks>
    public IReadOnlyList<string> Characters => Graphemes.Of(Text);
}

/// <summary>Grapheme cluster helpers: the unit Swift's <c>Character</c> counts.</summary>
internal static class Graphemes
{
    /// <summary>Split the string into grapheme clusters.</summary>
    internal static IReadOnlyList<string> Of(string text)
    {
        var elements = new List<string>();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add((string)enumerator.Current);
        }

        return elements;
    }

    /// <summary>Swift's <c>Character.isLetter</c> looks at the first scalar; so does this.</summary>
    internal static bool IsLetter(string cluster) =>
        cluster.Length > 0 && char.IsLetter(cluster, 0);

    /// <summary>Swift's <c>Character.isNumber</c>.</summary>
    internal static bool IsNumber(string cluster) =>
        cluster.Length > 0 && char.IsNumber(cluster, 0);

    internal static bool IsWhitespace(string cluster) =>
        cluster.Length > 0 && char.IsWhiteSpace(cluster, 0);
}

internal static class TextNormalizer
{
    /// <summary>Characters that are scored separately from words.</summary>
    internal static readonly HashSet<string> Punctuation = new(StringComparer.Ordinal)
    {
        ".", ",", "!", "?", ":", ";", "—", "–", "…", "«", "»", "\"", "(", ")", "-",
    };

    /// <summary>
    /// Symbols the model puts in place of a word: "75%" instead of "75 процентов". Separated
    /// from the word so the number does not stick to the symbol, but they do not go into the
    /// punctuation score - these are not punctuation marks.
    /// </summary>
    internal static readonly HashSet<string> Symbols = new(StringComparer.Ordinal)
    {
        "%", "$", "€", "₽", "№", "+", "=", "/", "*", "#",
    };

    internal static HashSet<string> Separators
    {
        get
        {
            var union = new HashSet<string>(Punctuation, StringComparer.Ordinal);
            union.UnionWith(Symbols);
            return union;
        }
    }

    /// <summary>
    /// Bring the text to a comparable form.
    ///
    /// What is done - and all of it is visible in the report, nothing happens silently:
    /// 1. case is folded to lower;
    /// 2. the Russian yo letter is folded to ye, because synthesis and the model diverge here
    ///    often without a change of meaning;
    /// 3. punctuation marks are separated from words into their own tokens;
    /// 4. numerals are folded to digits (see <see cref="NumeralFolder"/>).
    /// </summary>
    internal static NormalizedText Normalize(string text)
    {
        string lowered = text.ToLowerInvariant()
            .Replace("ё", "е", StringComparison.Ordinal)
            .Replace("’", "'", StringComparison.Ordinal);

        var tokens = new List<string>();
        var current = new StringBuilder();
        IReadOnlyList<string> characters = Graphemes.Of(lowered);
        HashSet<string> separators = Separators;

        void FlushWord()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        for (int index = 0; index < characters.Count; index++)
        {
            string character = characters[index];
            if (Graphemes.IsWhitespace(character))
            {
                FlushWord();
                continue;
            }

            if (separators.Contains(character))
            {
                // A hyphen and an apostrophe inside a word are part of the word: "тайп-скрипт",
                // "don't". The same symbol between spaces is a punctuation mark.
                bool neighbours = index > 0 && index + 1 < characters.Count;
                bool isInnerWord = (character == "-" || character == "'")
                    && neighbours
                    && (Graphemes.IsLetter(characters[index - 1]) || Graphemes.IsNumber(characters[index - 1]))
                    && Graphemes.IsLetter(characters[index + 1]);
                // A fractional separator is part of the number: "3.5", "2,01".
                bool isInnerNumber = (character == "." || character == ",")
                    && neighbours
                    && Graphemes.IsNumber(characters[index - 1])
                    && Graphemes.IsNumber(characters[index + 1]);
                if (isInnerWord || isInnerNumber)
                {
                    current.Append(character);
                    continue;
                }

                FlushWord();
                tokens.Add(character);
                continue;
            }

            current.Append(character);
        }

        FlushWord();

        bool IsSeparator(string token) => separators.Contains(token);

        var marks = tokens.Where(Punctuation.Contains).ToList();
        IReadOnlyList<string> words = NumeralFolder.Fold(tokens.Where(token => !IsSeparator(token)).ToList());

        // The numbers are folded in the general token flow as well, otherwise the two metrics
        // would be counting different things.
        var folded = new List<string>();
        var buffer = new List<string>();
        foreach (string token in tokens)
        {
            if (IsSeparator(token))
            {
                folded.AddRange(NumeralFolder.Fold(buffer));
                buffer.Clear();
                folded.Add(token);
            }
            else
            {
                buffer.Add(token);
            }
        }

        folded.AddRange(NumeralFolder.Fold(buffer));

        return new NormalizedText(words, folded, marks);
    }
}

/// <summary>The result of comparing one recording against a reference.</summary>
internal sealed record ScoreReport(
    ScoreReport.Counts Words,
    ScoreReport.Counts WordsWithPunctuation,
    double CharacterErrorRate,
    ScoreReport.Counts Punctuation,
    NormalizedText Reference,
    NormalizedText Hypothesis,
    IReadOnlyList<ScoreReport.Difference> Differences)
{
    internal struct Counts
    {
        public int Substitutions { get; set; }

        public int Deletions { get; set; }

        public int Insertions { get; set; }

        public int Correct { get; set; }

        public readonly int Errors => Substitutions + Deletions + Insertions;

        public readonly int ReferenceLength => Substitutions + Deletions + Correct;

        /// <summary>The share of errors. On an empty reference any insertion counts as an error.</summary>
        public readonly double Rate
        {
            get
            {
                int baseline = ReferenceLength;
                if (baseline <= 0)
                {
                    return Insertions > 0 ? 1 : 0;
                }

                return (double)Errors / baseline;
            }
        }
    }

    internal abstract record Difference
    {
        public sealed record Substitution(string Reference, string Hypothesis) : Difference
        {
            public override string Description => $"«{Reference}» → «{Hypothesis}»";
        }

        public sealed record Deletion(string Word) : Difference
        {
            public override string Description => $"Missing: {Word}";
        }

        public sealed record Insertion(string Word) : Difference
        {
            public override string Description => $"Extra: {Word}";
        }

        public abstract string Description { get; }
    }
}

internal static class TranscriptScorer
{
    internal static ScoreReport Score(string reference, string hypothesis)
    {
        NormalizedText referenceText = TextNormalizer.Normalize(reference);
        NormalizedText hypothesisText = TextNormalizer.Normalize(hypothesis);

        (ScoreReport.Counts wordCounts, IReadOnlyList<ScoreReport.Difference> differences) =
            Align(referenceText.Words, hypothesisText.Words);
        (ScoreReport.Counts tokenCounts, _) = Align(referenceText.Tokens, hypothesisText.Tokens);
        (ScoreReport.Counts markCounts, _) = Align(referenceText.Marks, hypothesisText.Marks);

        IReadOnlyList<string> referenceCharacters = referenceText.Characters;
        int characterDistance = Distance(referenceCharacters, hypothesisText.Characters);
        double characterErrorRate;
        if (referenceCharacters.Count == 0)
        {
            characterErrorRate = characterDistance > 0 ? 1 : 0;
        }
        else
        {
            characterErrorRate = (double)characterDistance / referenceCharacters.Count;
        }

        return new ScoreReport(
            wordCounts,
            tokenCounts,
            characterErrorRate,
            markCounts,
            referenceText,
            hypothesisText,
            differences);
    }

    /// <summary>
    /// Levenshtein distance with path restoration.
    ///
    /// The path is kept in a separate matrix of one byte per cell: on a half-hour recording
    /// that is about five thousand words per side, that is twenty-five megabytes - tolerable.
    /// The path is not restored for characters (see <see cref="Distance"/>): there the same
    /// matrix would weigh gigabytes.
    /// </summary>
    internal static (ScoreReport.Counts Counts, IReadOnlyList<ScoreReport.Difference> Differences) Align(
        IReadOnlyList<string> reference,
        IReadOnlyList<string> hypothesis)
    {
        int rows = reference.Count;
        int columns = hypothesis.Count;

        if (rows == 0)
        {
            var empty = new ScoreReport.Counts { Insertions = columns };
            return (empty, hypothesis.Select(word => (ScoreReport.Difference)new ScoreReport.Difference.Insertion(word)).ToList());
        }

        if (columns == 0)
        {
            var empty = new ScoreReport.Counts { Deletions = rows };
            return (empty, reference.Select(word => (ScoreReport.Difference)new ScoreReport.Difference.Deletion(word)).ToList());
        }

        const byte match = 0;
        const byte substitute = 1;
        const byte delete = 2;
        const byte insert = 3;
        int width = columns + 1;
        byte[] operations = new byte[(rows + 1) * width];
        Array.Fill(operations, insert);
        for (int row = 1; row <= rows; row++)
        {
            operations[row * width] = delete;
        }

        int[] previous = new int[width];
        for (int column = 0; column <= columns; column++)
        {
            previous[column] = column;
        }

        int[] current = new int[width];

        for (int row = 1; row <= rows; row++)
        {
            current[0] = row;
            for (int column = 1; column <= columns; column++)
            {
                bool same = reference[row - 1] == hypothesis[column - 1];
                int diagonal = previous[column - 1] + (same ? 0 : 1);
                int deletion = previous[column] + 1;
                int insertion = current[column - 1] + 1;

                int best = diagonal;
                byte operation = same ? match : substitute;
                if (deletion < best)
                {
                    best = deletion;
                    operation = delete;
                }

                if (insertion < best)
                {
                    best = insertion;
                    operation = insert;
                }

                current[column] = best;
                operations[(row * width) + column] = operation;
            }

            (previous, current) = (current, previous);
        }

        var counts = default(ScoreReport.Counts);
        var differences = new List<ScoreReport.Difference>();
        int backRow = rows;
        int backColumn = columns;

        while (backRow > 0 || backColumn > 0)
        {
            switch (operations[(backRow * width) + backColumn])
            {
                case match:
                    counts.Correct += 1;
                    backRow -= 1;
                    backColumn -= 1;
                    break;
                case substitute:
                    counts.Substitutions += 1;
                    differences.Add(new ScoreReport.Difference.Substitution(
                        reference[backRow - 1],
                        hypothesis[backColumn - 1]));
                    backRow -= 1;
                    backColumn -= 1;
                    break;
                case delete:
                    counts.Deletions += 1;
                    differences.Add(new ScoreReport.Difference.Deletion(reference[backRow - 1]));
                    backRow -= 1;
                    break;
                default:
                    counts.Insertions += 1;
                    differences.Add(new ScoreReport.Difference.Insertion(hypothesis[backColumn - 1]));
                    backColumn -= 1;
                    break;
            }
        }

        differences.Reverse();
        return (counts, differences);
    }

    /// <summary>Distance only, no path: two rows of memory instead of a matrix.</summary>
    internal static int Distance(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        if (reference.Count == 0)
        {
            return hypothesis.Count;
        }

        if (hypothesis.Count == 0)
        {
            return reference.Count;
        }

        int[] previous = new int[hypothesis.Count + 1];
        for (int column = 0; column <= hypothesis.Count; column++)
        {
            previous[column] = column;
        }

        int[] current = new int[hypothesis.Count + 1];

        for (int row = 1; row <= reference.Count; row++)
        {
            current[0] = row;
            for (int column = 1; column <= hypothesis.Count; column++)
            {
                bool same = reference[row - 1] == hypothesis[column - 1];
                current[column] = Math.Min(
                    Math.Min(previous[column - 1] + (same ? 0 : 1), previous[column] + 1),
                    current[column - 1] + 1);
            }

            (previous, current) = (current, previous);
        }

        return previous[hypothesis.Count];
    }
}
