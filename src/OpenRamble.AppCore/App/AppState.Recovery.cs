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
    /// Put the last dictation back on the clipboard.
    ///
    /// The text as it was inserted, not the raw words: this is the "I need that again" action,
    /// and what the person saw appear is what they mean.
    ///
    /// The write is host-only, exactly like the insertion: a plain write would hand the dictation
    /// to Clipboard History and Cloud Clipboard.
    /// </summary>
    public void CopyLastDictation()
    {
        string? text = LastDictationValue?.InsertedText;
        if (text is null || text.Trim().Length == 0)
        {
            Notify(new DictationNotice(DictationNoticeKind.Info, "Nothing dictated yet."));
            return;
        }

        try
        {
            clipboard?.Copy(text);
            Notify(new DictationNotice(DictationNoticeKind.Info, "Copied — to this PC only."));
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't copy the dictation."));
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
                // the PREVIOUS dictation, and after inserting recovered words neither "Copy Last
                // Dictation" nor correction learning may act on it - one would hand back someone
                // else's text, the other would protect someone else's spans.
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
