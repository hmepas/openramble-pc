namespace DictationCore;

/// <summary>
/// Selecting the edit of the inserted fragment from the contents of another field.
///
/// Given: what the field was like immediately after insertion, where our fragment is in it and what
/// the field became later. The common prefix and suffix of the two states are cut off; if
/// everything that has changed lies inside our fragment - this is an edit of the dictation.
/// Edits of someone else's text and additions after insertion do not concern this
/// construction: their modified middle goes beyond the boundaries of the fragment.
/// </summary>
/// <remarks>
/// Every offset here counts grapheme clusters (Swift's <c>Character</c>), never UTF-16
/// <c>char</c>: the fragment is searched for and cut in the same unit, otherwise an emoji
/// in someone else's text would shift the cut.
/// </remarks>
public static class InsertedEditExtractor
{
    public sealed record Edit
    {
        public Edit(string original, string edited)
        {
            Original = original;
            Edited = edited;
        }

        /// <summary>The fragment as it was inserted.</summary>
        public string Original { get; }

        /// <summary>The fragment after human editing.</summary>
        public string Edited { get; }
    }

    public static Edit? Extract(string baseline, string current, string inserted)
    {
        if (baseline == current || inserted.Length == 0) { return null; }

        string[] baselineChars = GraphemeText.Split(baseline);
        string[] currentChars = GraphemeText.Split(current);
        string[] insertedChars = GraphemeText.Split(inserted);

        // The fragment is searched from the end: insertion was the last operation, and when
        // the text repeats in the field, our instance is the one closest to the tail.
        int insertedStart = LastIndexOf(baselineChars, insertedChars);
        if (insertedStart < 0) { return null; }
        int insertedEnd = insertedStart + insertedChars.Length;

        int prefix = 0;
        while (prefix < baselineChars.Length && prefix < currentChars.Length
               && baselineChars[prefix] == currentChars[prefix])
        {
            prefix += 1;
        }
        int suffix = 0;
        while (suffix < baselineChars.Length - prefix && suffix < currentChars.Length - prefix
               && baselineChars[baselineChars.Length - 1 - suffix]
                   == currentChars[currentChars.Length - 1 - suffix])
        {
            suffix += 1;
        }

        // Changed baseline in character indices.
        int changedStart = prefix;
        int changedEnd = baselineChars.Length - suffix;

        // The edit must lie entirely within the inserted fragment.
        if (changedStart < insertedStart || changedEnd > insertedEnd) { return null; }
        if (changedStart == changedEnd)
        {
            // Nothing was deleted from baseline - the person just ADDED text.
            // Adding at the border of a fragment is adding your own, not
            // our edit: only an insertion strictly inside is considered an edit.
            if (changedStart <= insertedStart || changedStart >= insertedEnd) { return null; }
        }

        // Fragment after editing: its length has changed by the difference in field lengths.
        int delta = currentChars.Length - baselineChars.Length;
        int editedStart = insertedStart;
        int editedEnd = insertedEnd + delta;
        if (editedStart < 0 || editedEnd > currentChars.Length || editedStart > editedEnd)
        {
            return null;
        }

        string edited = string.Concat(currentChars[editedStart..editedEnd]);
        if (edited.Length == 0) { return null; }
        return new Edit(original: inserted, edited: edited);
    }

    /// <summary>
    /// The port of Swift's <c>range(of:options: .backwards)</c>: the last occurrence,
    /// compared grapheme cluster by grapheme cluster.
    /// </summary>
    private static int LastIndexOf(string[] haystack, string[] needle)
    {
        for (int start = haystack.Length - needle.Length; start >= 0; start -= 1)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle)) { return start; }
        }

        return -1;
    }
}
