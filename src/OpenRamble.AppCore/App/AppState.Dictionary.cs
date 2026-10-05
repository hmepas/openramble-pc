using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>The personal dictionary: the only thing a person types into this application by hand.</summary>
public sealed partial class AppState
{
    /// <summary>
    /// One pipeline for both paths: live dictation and replay after a failure.
    ///
    /// There used to be two, and they were already diverging - the replay was losing the trailing
    /// command. A shared builder makes "both paths are the same" a property of the code rather
    /// than something to remember while editing one of two places.
    /// </summary>
    private ITextProcessing MakePipeline() =>
        // The safe built-in technical vocabulary is part of recognition, not a hidden setup
        // chore. Personal entries win when they use the same heard spelling. The trailing
        // space is a wrapper rather than a stage: a personal preference has no business inside
        // the shared rule set.
        new TrailingSpacePipeline(
            new TextPipeline([.. StarterDictionary.Missing(Replacements), .. Replacements]),
            AppendsTrailingSpace);

    /// <summary>
    /// Learn from edits to inserted text noticed in the target application.
    ///
    /// Word-by-word diff with a conservative filter: only terms are learned (a change of
    /// spelling, a brand's capitalisation); speech edits do not count.
    /// </summary>
    public int Learn(string original, string edited)
    {
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original,
            edited,
            Replacements,
            // Spans of the last dictation: an edit inside a path or backticks is code editing,
            // and it will not become a silent rule for future dictations.
            LastDictationValue?.Provenance.Spans ?? []);

        foreach (DictionaryReplacement proposal in proposals)
        {
            AddReplacement(proposal.Spoken, proposal.Written);
        }

        if (proposals.Count > 0)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Info,
                proposals.Count == 1
                    ? "Learned 1 replacement from your edit."
                    : $"Learned {proposals.Count} replacements from your edit."));
        }

        return proposals.Count;
    }

    public int LearnCorrections(string editedText)
    {
        if (LastDictationValue is not LastDictation last) { return 0; }

        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            last.InsertedText,
            editedText,
            Replacements,
            last.Provenance.Spans);

        foreach (DictionaryReplacement proposal in proposals)
        {
            AddReplacement(proposal.Spoken, proposal.Written);
        }

        if (proposals.Count > 0)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Info,
                proposals.Count == 1
                    ? "Learned 1 replacement for future dictations."
                    : $"Learned {proposals.Count} replacements for future dictations."));
        }

        return proposals.Count;
    }

    /// <summary>The edit produced no replacement - say so, do not close the window in silence.</summary>
    public void NotifyNothingLearned() =>
        Notify(new DictationNotice(
            DictationNoticeKind.Info,
            "No new terms to learn — only term-like corrections become replacements."));

    public void AddReplacement(string spoken, string written)
    {
        spoken = spoken.Trim();
        written = written.Trim();
        if (spoken.Length == 0 || written.Length == 0) { return; }
        UpdateReplacements([.. Replacements, new DictionaryReplacement(spoken, written)]);
    }

    public void RemoveReplacements(IEnumerable<int> offsets)
    {
        var removed = new HashSet<int>(offsets);
        UpdateReplacements([.. Replacements.Where((_, index) => !removed.Contains(index))]);
    }

    /// <summary>
    /// The dictionary as a portable file — personal terms are the one piece of dictation state
    /// worth carrying to another machine.
    /// </summary>
    public byte[] ExportedDictionary() => DictionaryTransfer.Export(Replacements);

    /// <summary>
    /// Merge a dictionary file into the current replacements.
    ///
    /// Imports never destroy: existing entries are edited in place when the file knows the same
    /// spoken phrase, everything else is appended. The outcome is always named — including
    /// "nothing changed".
    /// </summary>
    public void ImportDictionary(byte[] data)
    {
        if (DictionaryProblem is ReplacementsStore.Problem problem)
        {
            Notify(new DictationNotice(DictationNoticeKind.Warning, problem.Message));
            return;
        }

        DictionaryTransfer.MergeResult merged;
        try
        {
            merged = DictionaryTransfer.Merge(Replacements, DictionaryTransfer.Read(data));
        }
        catch (DictionaryTransfer.TransferError error)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, error.Message));
            return;
        }

        if (merged.Added == 0 && merged.Updated == 0)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Info,
                "Nothing to import — every phrase in the file is already in the dictionary."));
            return;
        }

        UpdateReplacements(merged.Replacements);

        // UpdateReplacements reports its own save failure and leaves the list untouched; announce
        // success only when the merge actually landed.
        if (!ReferenceEquals(Replacements, merged.Replacements)) { return; }

        Notify(new DictationNotice(
            DictationNoticeKind.Info,
            ImportSummary(merged.Added, merged.Updated)));
    }

    /// <summary>
    /// The file dialogs live in the view; their failures surface through the same channel as
    /// every other notice.
    /// </summary>
    public void ReportDictionaryFileProblem(string message) =>
        Notify(new DictationNotice(DictationNoticeKind.Failure, message));

    private static string ImportSummary(int added, int updated)
    {
        static string Phrases(int count) => count == 1 ? "1 phrase" : $"{count} phrases";

        return (added > 0, updated > 0) switch
        {
            (true, true) => $"Imported {Phrases(added)}, updated {Phrases(updated)}.",
            (true, false) => $"Imported {Phrases(added)}.",
            (false, true) => $"Updated {Phrases(updated)} from the file.",
            (false, false) => "Nothing to import.",
        };
    }

    /// <remarks>
    /// The macOS build also rebuilds the acoustic boost here, so a dictionary edit reaches the
    /// recognizer without a restart. This port has no acoustic hinter to rebuild - the second
    /// model has no ONNX export - so a dictionary edit affects the text pipeline only, which is
    /// the whole of the feature that exists here. Nothing is silently half-applied.
    /// </remarks>
    private void UpdateReplacements(IReadOnlyList<DictionaryReplacement> updated)
    {
        if (DictionaryProblem is ReplacementsStore.Problem problem)
        {
            Notify(new DictationNotice(DictationNoticeKind.Warning, problem.Message));
            return;
        }

        try
        {
            replacementsStore.Save(updated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // It is not saved, which means it may not change in memory either: the list on the
            // screen would differ from the one on disk, and the person would find out only after
            // a restart.
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                $"The dictionary wasn't saved: {error.Message}"));
            return;
        }

        Replacements = updated;
    }
}
