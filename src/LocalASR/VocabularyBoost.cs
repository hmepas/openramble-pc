using DictationCore;

namespace LocalASR;

/// <summary>
/// Terms that recognition must recognize at the sound level.
///
/// This is not a replacement dictionary: replacements fix the text after recognition, and
/// these terms go into the acoustic prompt (CTC keyword spotting) and change the result
/// itself. The type lives separately from the engine, so that the rules for preparing the
/// lists are checked without a model.
/// </summary>
public sealed record VocabularyBoost
{
    public sealed record Term
    {
        /// <summary>
        /// Aliases are normalized like terms: empty ones are thrown away, the edges are
        /// trimmed, duplicates and matches of the term itself collapse.
        /// </summary>
        public Term(string text, IReadOnlyList<string>? aliases = null)
        {
            Text = text;
            var seen = new HashSet<string> { text.ToLowerInvariant() };
            var prepared = new List<string>();
            foreach (string raw in aliases ?? [])
            {
                string alias = raw.Trim();
                if (alias.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(alias.ToLowerInvariant()))
                {
                    continue;
                }

                prepared.Add(alias);
            }

            Aliases = prepared;
        }

        public string Text { get; }

        /// <summary>
        /// Alternative spellings of the same term - first of all Cyrillic ones: the model's
        /// text inside a Russian phrase is Cyrillic, and without such a bridge there is no
        /// candidate for replacement at all.
        /// </summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
        public bool Equals(Term? other) =>
            other is not null && Text == other.Text && Aliases.SequenceEqual(other.Aliases);

        public override int GetHashCode() => HashCode.Combine(Text, Aliases.Count);
    }

    /// <summary>
    /// Empty lines are discarded, whitespace is trimmed, duplicates collapse
    /// case-insensitively - the first spelling wins: the person entered it themselves.
    /// </summary>
    public VocabularyBoost(
        IReadOnlyList<Term> terms,
        float minSimilarity = 0.65f,
        float biasWeight = 3.0f)
    {
        var seen = new HashSet<string>();
        var prepared = new List<Term>();
        foreach (Term term in terms)
        {
            string text = term.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!seen.Add(text.ToLowerInvariant()))
            {
                continue;
            }

            prepared.Add(new Term(text, term.Aliases));
        }

        Terms = prepared;
        MinSimilarity = Math.Min(1, Math.Max(0, minSimilarity));
        BiasWeight = biasWeight;
    }

    public IReadOnlyList<Term> Terms { get; }

    /// <summary>
    /// How similar a word in the text has to be to a term or an alias to qualify for
    /// replacement (0…1). Lower - wider coverage and more false triggers in ordinary speech;
    /// higher - more careful. The value was chosen by measuring on a corpus: see
    /// docs/benchmarks.md.
    /// </summary>
    public float MinSimilarity { get; }

    /// <summary>
    /// To what extent the acoustic evidence of a term should outweigh the original word.
    /// More - the replacement is more aggressive; ordinary Russian speech that sounds like a
    /// term suffers first.
    /// </summary>
    public float BiasWeight { get; }

    public bool IsEmpty => Terms.Count == 0;

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(VocabularyBoost? other) =>
        other is not null
        && Terms.SequenceEqual(other.Terms)
        && MinSimilarity.Equals(other.MinSimilarity)
        && BiasWeight.Equals(other.BiasWeight);

    public override int GetHashCode() => HashCode.Combine(Terms.Count, MinSimilarity, BiasWeight);

    /// <summary>
    /// Terms that a person keeps away from acoustics.
    ///
    /// There used to be a hardcoded list of three words here. Now the entry itself decides
    /// through <see cref="DictionaryReplacement.NoAcousticBoost"/>, because everyone has
    /// their own terms: "касса" catches "Kassa", "сани" catches "Sunny", and our list of
    /// three words knew nothing about them. The reason such terms are excluded at all is in
    /// the documentation of the flag itself.
    /// </summary>
    internal static HashSet<string> Unboostable(IReadOnlyList<DictionaryReplacement> replacements) =>
        replacements
            .Where(replacement => replacement.NoAcousticBoost)
            .Select(replacement => replacement.Written.Trim().ToLowerInvariant())
            .ToHashSet();

    /// <summary>
    /// A set according to the user's dictionary: starter terms plus their own replacements -
    /// including those learned from edits.
    /// </summary>
    public static VocabularyBoost WithUserReplacements(
        IReadOnlyList<DictionaryReplacement> replacements)
    {
        VocabularyBoost defaults = DeveloperDefault();
        var known = defaults.Terms.Select(term => term.Text.ToLowerInvariant()).ToHashSet();
        // Marked by a person plus marked in the starter set: a custom record
        // "деплой → deploy" has no right to return deploy to acoustics only because it
        // carries no checkmark.
        // A personal text-only mark blocks that canonical term completely. For built-ins,
        // block only canonical terms with no safe acoustic spelling; one exact-only repair
        // must not suppress the other measured aliases.
        var starterWritten = StarterDictionary.Developer
            .Select(replacement => replacement.Written.ToLowerInvariant())
            .ToHashSet();
        starterWritten.ExceptWith(known);
        HashSet<string> blocked = Unboostable(replacements);
        blocked.UnionWith(starterWritten);

        // The text pipeline gives a personal rule ownership of its heard spelling. Give the
        // acoustic pipeline the same precedence: one alias must never point at both a
        // built-in term and a different personal canonical value, or the built-in may win
        // before text replacement can see the original spelling.
        var personalAliasOwner = new Dictionary<string, string>();
        foreach (DictionaryReplacement replacement in replacements)
        {
            string canonical = replacement.Written.Trim().ToLowerInvariant();
            string alias = replacement.Spoken.Trim().ToLowerInvariant();
            if (canonical.Length == 0 || alias.Length == 0)
            {
                continue;
            }

            if (!replacement.Written.Any(character => char.IsLetter(character) && char.IsAscii(character)))
            {
                continue;
            }

            personalAliasOwner.TryAdd(alias, canonical);
        }

        // Preserve the person's entry order while grouping aliases by canonical spelling. A
        // personal pronunciation of an existing built-in term must extend that term instead
        // of being discarded as a duplicate.
        var grouped = new List<(string Key, string Text, List<string> Aliases)>();
        var groupedIndex = new Dictionary<string, int>();
        foreach (DictionaryReplacement replacement in replacements)
        {
            string text = replacement.Written.Trim();
            string key = text.ToLowerInvariant();
            if (blocked.Contains(key))
            {
                continue;
            }

            if (!text.Any(character => char.IsLetter(character) && char.IsAscii(character)))
            {
                continue;
            }

            string aliasKey = replacement.Spoken.Trim().ToLowerInvariant();
            if (!personalAliasOwner.TryGetValue(aliasKey, out string? owner) || owner != key)
            {
                continue;
            }

            if (groupedIndex.TryGetValue(key, out int index))
            {
                grouped[index].Aliases.Add(replacement.Spoken);
            }
            else
            {
                groupedIndex[key] = grouped.Count;
                grouped.Add((key, text, [replacement.Spoken]));
            }
        }

        var mergedDefaults = new List<Term>();
        foreach (Term term in defaults.Terms)
        {
            string key = term.Text.ToLowerInvariant();
            if (blocked.Contains(key))
            {
                continue;
            }

            if (personalAliasOwner.TryGetValue(key, out string? canonicalOwner) && canonicalOwner != key)
            {
                continue;
            }

            var aliases = term.Aliases
                .Where(alias =>
                    !personalAliasOwner.TryGetValue(alias.Trim().ToLowerInvariant(), out string? owner)
                    || owner == key)
                .ToList();

            mergedDefaults.Add(groupedIndex.TryGetValue(key, out int index)
                ? new Term(term.Text, [.. aliases, .. grouped[index].Aliases])
                : new Term(term.Text, aliases));
        }

        var userTerms = grouped
            .Where(group => !known.Contains(group.Key))
            .Select(group => new Term(group.Text, group.Aliases))
            .ToList();
        return new VocabularyBoost([.. mergedDefaults, .. userTerms]);
    }

    /// <summary>
    /// A ready-made set for the developer's dictionary: the Latin term plus its Cyrillic
    /// spellings as aliases - a bridge from sound to Latin.
    /// </summary>
    public static VocabularyBoost DeveloperDefault()
    {
        IReadOnlyList<DictionaryReplacement> starter = StarterDictionary.Developer;
        var terms = starter
            .GroupBy(replacement => replacement.Written)
            .Select(group =>
            {
                // `NoAcousticBoost` belongs to a spelling. A measured exact decoder repair
                // may be unsafe as an acoustic alias while the canonical term and its normal
                // aliases remain useful.
                var aliases = group.Where(replacement => !replacement.NoAcousticBoost).ToList();
                return aliases.Count == 0
                    ? null
                    : new Term(group.Key, aliases.Select(replacement => replacement.Spoken).ToList());
            })
            .OfType<Term>()
            .OrderBy(term => term.Text, StringComparer.Ordinal)
            .ToList();
        return new VocabularyBoost(terms);
    }
}

/// <summary>
/// The problem is with the list of terms, not with the model.
///
/// A separate type exists for the sake of one difference the user sees with their own eyes:
/// <c>ASREngineError.ModelsUnavailable</c> means "the weights are bad, they need downloading",
/// and the application honestly offers a 483 MB repair. A term that cannot be turned into
/// tokens is human data; repairing the model will not fix it, and the offer to do so misleads
/// and costs the user traffic. Catch this type separately and show dictionary editing rather
/// than model restoration.
/// </summary>
public sealed class VocabularyBoostError : Exception
{
    /// <summary>
    /// The term with this number (from one) cannot be expanded into tokens. The text of the
    /// term itself does not appear in the error: the contents of the dictionary are the same
    /// person's data as the dictation text.
    /// </summary>
    public VocabularyBoostError(int index)
        : base($"the term #{index} cannot be tokenized")
    {
        Index = index;
    }

    public int Index { get; }
}

/// <summary>
/// An engine that can show recognition live while a person is speaking.
///
/// Preview text is for the eyes only: the source of truth remains the batch recognition of
/// the finished recording by the same engine.
/// </summary>
public interface ILivePreviewCapable
{
    /// <summary>
    /// <paramref name="onUpdate"/> receives the settled text and the tail that can still
    /// change. Updates arrive no more than four times per second: flickering is more
    /// distracting than the delay.
    /// </summary>
    Task StartPreview(Action<string, string> onUpdate);

    Task FeedPreview(float[] samples);

    Task StopPreview();
}

/// <summary>
/// An engine that can accept acoustic hints of terms.
///
/// The interface lives in LocalASR, not in DictationCore: the dictation core does not care
/// where the text came from, and the ability to "recognize terms by sound" is a property of a
/// specific engine.
/// </summary>
/// <remarks>
/// Swift conforms <c>FluidAudioAdapter</c> to this protocol in this very file. The engine
/// adapter is not ported yet (sherpa-onnx arrives in the next wave), so the conformance
/// declarations have no counterpart here - the interfaces themselves are what
/// <see cref="LocalTranscriber"/> probes for.
/// </remarks>
public interface IVocabularyBoostCapable
{
    Task LoadVocabularyModels(string directory, VocabularyBoost boost);
}
