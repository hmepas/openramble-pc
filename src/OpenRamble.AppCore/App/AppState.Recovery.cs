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
    /// Copy one of the menu's takes without opening another window or writing history to disk.
    /// Host-only, like every copy this app makes. Successful copies stay quiet; the menu action
    /// itself is sufficient feedback.
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

    // MARK: - The menu's takes

    /// <summary>
    /// The newest takes of this session, newest first: the menu's first rows. The first is the
    /// last take, the one the last-take shortcut pastes. The same words twice are one row.
    /// </summary>
    public IReadOnlyList<RecentDictation> MenuTakes =>
        [.. RecentDictations.DistinctBy(entry => entry.Text).Take(MenuSections.TakeLimit)];

    /// <summary>
    /// A take's row: its words, in quotes, on one line. The last take carries the shortcut that
    /// pastes it - written into the title, as no tray menu here can show a global chord.
    /// </summary>
    public string TakeMenuTitle(RecentDictation take, bool isLastTake) =>
        isLastTake && LastTakeShortcut is { } shortcut
            ? $"\u201C{take.MenuTitle}\u201D   {shortcut.DisplayString(shortcutKeymap)}"
            : $"\u201C{take.MenuTitle}\u201D";

    /// <summary>
    /// Paste the last take into the app in front: the last-take shortcut and the menu's first row.
    /// </summary>
    public void PasteLastTake()
    {
        IReadOnlyList<RecentDictation> takes = MenuTakes;
        if (takes.Count == 0)
        {
            Notify(new DictationNotice(DictationNoticeKind.Info, "Nothing dictated yet."));
            return;
        }

        PasteTake(takes[0]);
    }

    /// <summary>
    /// Paste one of the menu's takes into the app in front, through the inserter every dictation
    /// uses. Only while nothing else is being dictated or inserted; over another paste it says so.
    /// </summary>
    public void PasteTake(RecentDictation take)
    {
        if (!RecentDictations.Any(entry => entry.Id == take.Id)) { return; }
        bool claimed;
        lock (gate)
        {
            if (dictationState != DictationState.Idle) { return; }
            claimed = TryClaimInsertionLocked(InsertionOwner.MenuTake);
        }

        if (!claimed)
        {
            NotifyStillPasting();
            return;
        }

        DictationStateValue = DictationState.Inserting;

        _ = Task.Run(async () =>
        {
            try
            {
                // Asked here, off the caller's thread: on Linux the answer is a round trip.
                TargetApplication? target = inserter.FrontmostApplication();
                // The words were shaped for the app they were dictated into; the app in front now
                // may be a terminal, which never gets a paragraph break.
                string fitted = ParagraphBreaks.Fit(take.Text, new PromptModePolicy().Mode(target));
                await overlay.Dismiss().ConfigureAwait(false);
                await inserter.Insert(fitted, target).ConfigureAwait(false);
            }
            catch (TextInsertionError.InsertedButClipboardRestoreFailed)
            {
                // Landed: the words are where the person is; only the clipboard is not.
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    "The text was inserted, but the previous clipboard couldn't be restored."));
            }
            catch (TextInsertionError)
            {
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    "The words couldn't be pasted. They stay in the menu."));
            }
            finally
            {
                DictationStateValue = DictationState.Idle;
                ReleaseInsertion(InsertionOwner.MenuTake);
            }
        });
    }

    // MARK: - Recovering a failed insertion

    public void RetryRecoveredText()
    {
        string text;
        bool claimed;
        lock (gate)
        {
            if (dictationState != DictationState.Idle || recoveredText is null) { return; }
            text = recoveredText;
            claimed = TryClaimInsertionLocked(InsertionOwner.Recovery);
        }

        if (!claimed)
        {
            NotifyStillPasting();
            return;
        }

        DictationStateValue = DictationState.Inserting;

        _ = Task.Run(async () =>
        {
            try
            {
                // Asked here, off the caller's thread: on Linux the answer is a round trip to the
                // compositor or two runs of xprop, and the caller is the menu's UI thread.
                TargetApplication? target = inserter.FrontmostApplication();
                // The words were shaped for the app they were dictated into; the app in front now
                // may be a terminal, which never gets a paragraph break. The menu keeps them as
                // they were, for a retry into a prose app.
                string fitted = ParagraphBreaks.Fit(text, new PromptModePolicy().Mode(target));
                await overlay.Dismiss().ConfigureAwait(false);
                await inserter.Insert(fitted, target).ConfigureAwait(false);

                // The words already joined Recent Dictations at failure time; appending again
                // here would duplicate them. The provenance slot is cleared too: it still holds
                // the PREVIOUS dictation, and after inserting recovered words neither "Copy Last
                // Dictation" nor correction learning may act on it - one would hand back someone
                // else's text, the other would protect someone else's spans.
                SuccessfulDictationCount += 1;
                RecoveredText = null;
                // The same words, if they also wait on the panel: landed, they wait no more.
                WaitingWordsLanded(text);
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
                ReleaseInsertion(InsertionOwner.Recovery);
            }
        });
    }
}
