using System.Runtime.InteropServices;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>The words after the dictation: history, copying, and a failed insertion's second chance.</summary>
public sealed partial class AppState
{
    private void RecordSuccessfulDictation(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) { return; }
        SuccessfulDictationCount += 1;
        RecoveredText = null;
        PrependRecentDictation(trimmed);
    }

    /// <summary>
    /// Words that failed to insert enter the history immediately.
    ///
    /// Idempotent across repeated failures of the same text: retrying the insertion and failing
    /// again must not fill the history with copies.
    /// </summary>
    private void RememberFailedInsertion(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) { return; }
        if (RecentDictations.Count > 0 && RecentDictations[0].Text == trimmed) { return; }
        PrependRecentDictation(trimmed);
    }

    private void PrependRecentDictation(string trimmed)
    {
        List<RecentDictation> updated = [new RecentDictation(trimmed), .. RecentDictations];
        if (updated.Count > 8) { updated.RemoveRange(8, updated.Count - 8); }
        RecentDictations = updated;
    }

    /// <summary>
    /// Copy a completed dictation without opening another window or writing history to disk.
    /// Successful copies stay quiet; the menu action itself is sufficient feedback.
    /// </summary>
    public void CopyRecentDictation(RecentDictation dictation)
    {
        if (!RecentDictations.Any(entry => entry.Id == dictation.Id)) { return; }
        try
        {
            clipboard?.Copy(dictation.Text);
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't copy the dictation."));
        }
    }

    /// <summary>
    /// Put what the person said into the clipboard - before the dictionary and before the
    /// cosmetics.
    ///
    /// The promise "the verbatim text is always available" rests on the provenance held in
    /// process memory: no reading of other people's fields and no disk write is needed for it.
    ///
    /// The write is host-only, exactly like the insertion: a plain write would hand the dictation
    /// to Clipboard History and Cloud Clipboard.
    /// </summary>
    public void CopyRawDictation()
    {
        string? raw = LastDictationValue?.Provenance.Raw;
        if (raw is null || raw.Trim().Length == 0) { return; }
        try
        {
            clipboard?.Copy(raw);
            Notify(new DictationNotice(DictationNoticeKind.Info, "Copied as spoken — to this PC only."));
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't copy the spoken text."));
        }
    }

    // MARK: - Recovering a failed insertion

    public void RetryRecoveredText()
    {
        string text;
        lock (gate)
        {
            if (dictationState != DictationState.Idle
                || isRecoveryOperationActive
                || recoveredText is null)
            {
                return;
            }

            text = recoveredText;
            isRecoveryOperationActive = true;
        }

        TargetApplication? target = inserter.FrontmostApplication();
        DictationStateValue = DictationState.Inserting;

        _ = Task.Run(async () =>
        {
            try
            {
                await overlay.Dismiss().ConfigureAwait(false);
                await inserter.Insert(text, target).ConfigureAwait(false);

                // The words already joined Recent Dictations at failure time; appending again
                // here would duplicate them. The provenance slot is cleared too: it still holds
                // the PREVIOUS dictation, and "Copy Last as Spoken" must not offer someone else's
                // raw text after inserting recovered words.
                SuccessfulDictationCount += 1;
                RecoveredText = null;
                LastDictationValue = null;
                await overlay.Dismiss().ConfigureAwait(false);
            }
            catch (TextInsertionError)
            {
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    "The text still couldn't be inserted — it stays in the menu.",
                    recoverableText: text));
            }
            finally
            {
                DictationStateValue = DictationState.Idle;
                lock (gate) { isRecoveryOperationActive = false; }
            }
        });
    }
}
