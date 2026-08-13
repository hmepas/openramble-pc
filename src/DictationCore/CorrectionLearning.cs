using System.Text;

namespace DictationCore;

/// <summary>
/// Dictionary training on the edits of the last dictation.
///
/// The editing surface is the application's own window: we do not read other people's
/// applications and do not monitor the keyboard, so the only honest signal is
/// the text that the person corrected for us himself. A word-by-word diff turns an edit
/// into candidates for replacement.
///
/// The filter is intentionally conservative. Only the pair that carries a
/// <b>signal of the term</b> is learned: a change of writing ("поуст герз" → "Postgres") or
/// a brand register ("github" → "GitHub"). Everything else is more of a speech edit
/// ("быстро" → "быстрее", "the file" → "a file"), and learning it would mean silently
/// replacing the person's words in the following dictations.
/// </summary>
public static class CorrectionLearning
{
    /// <summary>
    /// How many replacements can come out of one edit.
    ///
    /// Editing a dictation is a few terms. When there are more pairs in front of us,
    /// it is not an edit, but a different text: an inserted translation, a rewritten paragraph.
    /// Nothing can be learned from that - not because some of the pairs are bad, but because
    /// establishing dozens of silent rules for future dictations at once is
    /// irreversible, while saying "there is nothing to learn" and asking to correct the terms
    /// in smaller portions is not. Measurement: an edit-substitution of 200 words gave
    /// 200 replacements at once.
    /// </summary>
    internal const int MaximumProposalsPerCorrection = 5;

    /// <summary>
    /// Above how many words an edit is not considered at all.
    ///
    /// The diff is an LCS table of <c>original × edited</c> cells, and it is built on
    /// the MainActor right after the insertion - while the person is still typing.
    /// Measurement before the ceiling: 2000 words - 0.92 s and 31 MB, 4000 - 3.36 s
    /// and 125 MB, 6000 - 7.50 s and 282 MB. The application beach-balled in the
    /// middle of the work.
    ///
    /// <see cref="MaximumProposalsPerCorrection"/> did not limit anything here: it is applied
    /// to the finished diff, that is, after the entire table has been paid for.
    /// And by the same doctrine, an edit of hundreds of words is not an edit of
    /// terms - it would be thrown away at the end anyway. So we say "there is
    /// nothing to learn" before the work, not after it.
    ///
    /// Four hundred is a dictation of a couple of paragraphs, more than any real
    /// term correction, and it costs about 40 ms and 1.3 MB - a bearable price
    /// for the MainActor. The table remains two-dimensional: the traceback reads
    /// it entirely, and two rows are not enough for that.
    /// </summary>
    internal const int MaximumWordsPerCorrection = 400;

    /// <summary>
    /// Suggest replacements for edits. It does not save anything - it only offers.
    ///
    /// <paramref name="spans"/> - protected spans of the source text. Proposals are never
    /// built from them: a path, a flag or the contents of backticks a person edits as
    /// code, not as a term, and turning such an edit into a silent rule of
    /// future dictations is not allowed. An empty list is the same behavior.
    /// </summary>
    public static IReadOnlyList<DictionaryReplacement> Propose(
        string original,
        string edited,
        IReadOnlyList<DictionaryReplacement>? existing = null,
        IReadOnlyList<ProtectedSpan>? spans = null)
    {
        List<string> originalWords = Words(original);
        List<string> editedWords = Words(edited);
        if (originalWords.Count == 0 || editedWords.Count == 0) { return []; }
        // The ceiling is checked before the diff, and not after it: the whole
        // point is not to build a table that will be thrown away anyway.
        if (originalWords.Count > MaximumWordsPerCorrection
            || editedWords.Count > MaximumWordsPerCorrection)
        {
            return [];
        }

        var known = (existing ?? [])
            .Select(replacement => replacement.Spoken.ToLowerInvariant())
            .ToHashSet();
        var protectedWords = (spans ?? [])
            .SelectMany(span => Words(span.Text))
            .Select(word => word.ToLowerInvariant())
            .ToHashSet();
        var seen = new HashSet<string>();
        var proposals = new List<DictionaryReplacement>();

        foreach (Substitution pair in Substitutions(originalWords, editedWords))
        {
            string spoken = string.Join(" ", pair.Original);
            string written = string.Join(" ", pair.Edited);
            if (spoken.ToLowerInvariant() == written.ToLowerInvariant() && spoken == written)
            {
                continue;
            }

            if (!LooksLikeTerm(spoken: spoken, written: written)) { continue; }
            if (known.Contains(spoken.ToLowerInvariant())) { continue; }
            // At least one word from the protected piece - the proposal is discarded
            // entirely: editing inside the code does not make the term.
            if (pair.Original.Any(word => protectedWords.Contains(word.ToLowerInvariant())))
            {
                continue;
            }

            // The same pair can appear twice in the text ("открой
            // сентри и закрой сентри"). The second copy in the dictionary adds
            // nothing, but a person would have to delete it on a separate line.
            if (!seen.Add($"{spoken.ToLowerInvariant()}\0{written}")) { continue; }
            // Terms from the edit are not inflected - just like in the starter
            // set: the inflected stem would invent matches.
            proposals.Add(new DictionaryReplacement(spoken: spoken, written: written, inflects: false));
        }

        if (proposals.Count > MaximumProposalsPerCorrection) { return []; }
        return proposals;
    }

    // MARK: - Diff

    private readonly record struct Substitution(List<string> Original, List<string> Edited);

    /// <summary>
    /// Pairs "replaced piece → what it was replaced with" according to the LCS alignment of words.
    ///
    /// Insertions and deletions do not generate replacements: a replacement is when there is
    /// something on both sides. Pieces longer than three words are discarded: that is already
    /// a rewritten sentence, not an edit of a term.
    /// </summary>
    private static List<Substitution> Substitutions(List<string> original, List<string> edited)
    {
        int[,] table = LcsTable(
            original.Select(word => word.ToLowerInvariant()).ToList(),
            edited.Select(word => word.ToLowerInvariant()).ToList());
        var result = new List<Substitution>();
        int i = original.Count;
        int j = edited.Count;
        var pendingOriginal = new List<string>();
        var pendingEdited = new List<string>();

        void Flush()
        {
            if (pendingOriginal.Count != 0 && pendingEdited.Count != 0
                && pendingOriginal.Count <= 3 && pendingEdited.Count <= 3)
            {
                pendingOriginal.Reverse();
                pendingEdited.Reverse();
                result.Add(new Substitution(pendingOriginal, pendingEdited));
            }

            pendingOriginal = [];
            pendingEdited = [];
        }

        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0
                && original[i - 1].ToLowerInvariant() == edited[j - 1].ToLowerInvariant()
                && original[i - 1] == edited[j - 1])
            {
                Flush();
                i -= 1;
                j -= 1;
            }
            else if (i > 0 && j > 0
                && original[i - 1].ToLowerInvariant() == edited[j - 1].ToLowerInvariant())
            {
                // The word is the same, but the case is different - this is also an edit.
                pendingOriginal.Add(original[i - 1]);
                pendingEdited.Add(edited[j - 1]);
                i -= 1;
                j -= 1;
            }
            else if (j > 0 && (i == 0 || table[i, j - 1] >= table[i - 1, j]))
            {
                pendingEdited.Add(edited[j - 1]);
                j -= 1;
            }
            else
            {
                pendingOriginal.Add(original[i - 1]);
                i -= 1;
            }
        }

        Flush();
        result.Reverse();
        return result;
    }

    private static int[,] LcsTable(List<string> a, List<string> b)
    {
        int[,] table = new int[a.Count + 1, b.Count + 1];
        for (int i = 1; i <= a.Count; i += 1)
        {
            for (int j = 1; j <= b.Count; j += 1)
            {
                table[i, j] = a[i - 1] == b[j - 1]
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table;
    }

    // MARK: - Filters

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (string character in GraphemeText.Split(text))
        {
            if (!IsLetter(character) && !IsNumber(character) && character != "-")
            {
                if (current.Length != 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length != 0) { words.Add(current.ToString()); }
        return words;
    }

    /// <summary>
    /// Does the correction look like the term for which the dictionary was created?
    ///
    /// You cannot check only for the Latin alphabet on the right: in a text that is entirely
    /// in Latin, this sign does not mean anything, and an ordinary edit of speech
    /// would pass for the term. Measurement: "please send me the file" → "…a file"
    /// gave the replacement "the" → "a", after which each subsequent dictation
    /// silently turned "The report is on the desk" into "A report is on a desk".
    ///
    /// Therefore, a signal is considered not to be the presence of the Latin alphabet, but its appearance where
    /// it was not there, or a register that an ordinary word does not have:
    ///
    /// <list type="number">
    /// <item><description><b>Change of writing</b> - there is no Latin on the left, there is on the right. Exactly
    /// what the term looks like when heard in Cyrillic: "поуст герз" → "Postgres".</description></item>
    /// <item><description><b>Brand register</b> - a capital not at the beginning of the word: "GitHub", "macOS",
    /// "API". Capitalizing only the first letter is not considered a signal: it
    /// is indistinguishable from the beginning of a sentence, and pairs like
    /// "The" → "the" come together with their opposite "the" → "The".</description></item>
    /// </list>
    ///
    /// One Latin letter is not enough for the term: "мир" → "a" formally replaces
    /// the writing, but as a replacement throughout the text it is too expensive.
    /// </summary>
    private static bool LooksLikeTerm(string spoken, string written)
    {
        if (GraphemeText.Split(written).Count(character => IsLetter(character) && IsASCII(character)) < 2)
        {
            return false;
        }

        bool spokenHasLatin = GraphemeText.Split(spoken)
            .Any(character => IsLetter(character) && IsASCII(character));
        bool writtenHasLatin = GraphemeText.Split(written)
            .Any(character => IsLetter(character) && IsASCII(character));
        if (!spokenHasLatin && writtenHasLatin) { return true; }

        return Words(written).Any(word =>
            GraphemeText.Split(word).Skip(1)
                .Any(character => IsUppercase(character) && IsASCII(character)));
    }

    // MARK: - Grapheme-cluster helpers
    //
    // Each predicate takes one grapheme cluster (Swift's `Character`) and answers about
    // its first scalar, the way Swift's `Character` properties do.

    private static Rune FirstRune(string character) =>
        Rune.TryGetRuneAt(character, 0, out Rune rune) ? rune : default;

    private static bool IsLetter(string character) => Rune.IsLetter(FirstRune(character));

    private static bool IsNumber(string character) => Rune.IsNumber(FirstRune(character));

    private static bool IsUppercase(string character) => Rune.IsUpper(FirstRune(character));

    /// <summary>A single ASCII scalar, exactly like Swift's <c>Character.isASCII</c>.</summary>
    private static bool IsASCII(string character) => character.Length == 1 && character[0] < 128;
}
