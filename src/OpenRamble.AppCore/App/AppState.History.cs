using System.Runtime.InteropServices;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>Finished dictations kept on disk, with their audio (upstream 0.9.0, 0.18.0, 0.19.0).</summary>
public sealed partial class AppState
{
    private DictationHistoryStore? historyStore;
    private IReadOnlyList<HistoryEntry> history = [];
    private int historyLimit;
    private bool isRetranscribing;

    /// <summary>Finished dictations kept on disk, newest first, with their audio.</summary>
    public IReadOnlyList<HistoryEntry> History
    {
        get { lock (gate) { return history; } }
        private set => Set(ref history, value);
    }

    /// <summary>
    /// How many takes the history keeps. Changing it trims what is already stored, so lowering
    /// the number is a deletion the person can rely on.
    /// </summary>
    public int HistoryLimit
    {
        get { lock (gate) { return historyLimit; } }
        set
        {
            int clamped = DictationHistoryStore.ClampLimit(value);
            if (!Set(ref historyLimit, clamped)) { return; }
            UpdateSettings(current => current with { HistoryLimit = clamped });
            if (historyStore is null) { return; }
            History = TryHistory(() => historyStore.ApplyLimit(clamped)) ?? History;
        }
    }

    /// <summary>
    /// One re-run at a time: the engine takes one call, and a queue of them would be a queue
    /// nobody asked for.
    /// </summary>
    public bool IsRetranscribing
    {
        get { lock (gate) { return isRetranscribing; } }
        private set => Set(ref isRetranscribing, value);
    }

    /// <summary>
    /// Keep a finished take: its text, and a copy of its audio. Empty text is a take with sound
    /// and no words, kept for its audio.
    ///
    /// Failures here are reported rather than swallowed. A history that silently stops
    /// recording looks exactly like a history of someone who stopped dictating.
    /// </summary>
    /// <returns>Whether History holds the take now - what a wordless take's notice says.</returns>
    private bool Archive(string text, string audio, TargetApplication? target)
    {
        if (historyStore is null) { return false; }
        // The kind of app, never its name: enough to shape the text again the same way.
        ApplicationCategory category = PromptModePolicy.Category(target);
        IReadOnlyList<HistoryEntry>? recorded = TryHistory(
            () => historyStore.Record(text, audio, HistoryLimit, now(), category),
            "history.record.failed");
        if (recorded is null) { return false; }
        History = recorded;
        return true;
    }

    /// <summary>The take's audio, if it is still on disk.</summary>
    public string? HistoryAudioPath(HistoryEntry entry) => historyStore?.AudioPath(entry);

    /// <summary>
    /// Open the folder holding the audio kept alongside history.
    ///
    /// Points at a recording rather than the bare folder when there is one, so the file manager
    /// opens with something to look at instead of leaving the person to work out which of these
    /// files is theirs.
    /// </summary>
    public void RevealHistoryAudio()
    {
        string? target = History.Select(HistoryAudioPath).FirstOrDefault(path => path is not null);
        if (target is null)
        {
            Notify(new DictationNotice(DictationNoticeKind.Info, "No recordings kept yet."));
            return;
        }

        try
        {
            folders?.Open(target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't open the recordings folder."));
        }
    }

    /// <summary>
    /// Recognise a kept recording again.
    ///
    /// Worth having after editing the dictionary: the words the model heard do not change, but
    /// what they become does, and re-running is the only way to see that without dictating the
    /// sentence a second time.
    ///
    /// The new text replaces the entry's text and nothing else. The audio stays where it is, so
    /// this can be done repeatedly, and a run that fails leaves the previous text intact rather
    /// than blanking a record of something the person actually said.
    ///
    /// A take that had no words and still has none says so, and keeps its row.
    ///
    /// The text is shaped for the kind of app the take went to, exactly as the take was:
    /// paragraphs (spoken and at pauses) where it went to prose, "Send it." left off where it
    /// went to a terminal. Only the text is kept; nothing is pressed or inserted.
    /// </summary>
    public void RetranscribeHistoryEntry(HistoryEntry entry)
    {
        if (IsRetranscribing) { return; }
        string? audio = HistoryAudioPath(entry);
        if (audio is null)
        {
            Notify(new DictationNotice(DictationNoticeKind.Info, "That dictation's audio is gone."));
            return;
        }

        lock (modelLineupGate)
        {
            // Under the lineup's lock, so a model switch cannot slip in between this check and
            // the work it lets through.
            if (transcriber is null || !IsEngineReady)
            {
                Notify(new DictationNotice(DictationNoticeKind.Info, "The speech model isn't ready yet."));
                return;
            }

            IsRetranscribing = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                ASRResult result = await transcriber.Transcribe(audio).ConfigureAwait(false);
                string text = MakePipeline(entry.AppCategory ?? ApplicationCategory.Unknown)
                    .Run(ParagraphBreaks.Mark(result.Text, result.Words))
                    .Output.Text;
                if (historyStore is null) { return; }
                if (entry.Text.Length == 0 && !RecognizedTextPolicy.CarriesWords(text))
                {
                    Notify(new DictationNotice(DictationNoticeKind.Info, "Still nothing recognized."));
                    return;
                }

                // An empty run keeps the words already there rather than blanking a record of
                // something the person said.
                if (text.Length == 0)
                {
                    Notify(new DictationNotice(
                        DictationNoticeKind.Info,
                        result.Text.Trim().Length != 0
                            ? "Nothing left after cleanup — the text was kept."
                            : "Nothing new was recognized — the text was kept."));
                    return;
                }

                History = TryHistory(() => historyStore.ReplaceText(text, entry)) ?? History;
            }
            catch (Exception error) when (error is ASREngineError or IOException or OperationCanceledException)
            {
                DiagnosticLog.EmitException("history.retranscribe.failed", error);
                Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't recognise that recording again."));
            }
            finally
            {
                IsRetranscribing = false;
                ApplyPendingModelSwitch();
            }
        });
    }

    /// <summary>Star an entry so retention leaves it alone, or unstar it.</summary>
    public void SetHistoryEntryKept(bool isKept, HistoryEntry entry)
    {
        if (historyStore is null) { return; }
        History = TryHistory(() => historyStore.SetKept(isKept, entry)) ?? History;
    }

    public void DeleteHistoryEntry(HistoryEntry entry)
    {
        if (historyStore is null) { return; }
        History = TryHistory(() => historyStore.Delete(entry)) ?? History;
    }

    public void ClearHistory()
    {
        if (historyStore is null) { return; }
        try
        {
            historyStore.DeleteAll();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.EmitException("history.clear.failed", error);
        }

        History = [];
        // Delete All means every copy of the words this app holds, not only the rows: the last
        // dictation behind "Copy Last" and the recent ones in the menu go with them.
        LastDictationValue = null;
        RecentDictations = [];
        RecoveredText = null;
    }

    /// <summary>
    /// Put a stored transcript back on the clipboard.
    ///
    /// Host-only, exactly like every other copy this app makes: a transcript must not travel
    /// into clipboard history just because it came from a list rather than from a live dictation.
    /// A wordless take has nothing to copy, and copying it anyway would put an empty string over
    /// whatever the person had on the clipboard - so it is a no-op, whatever a row's button offers.
    /// </summary>
    public void CopyHistoryEntry(HistoryEntry entry)
    {
        if (entry.Text.Length == 0) { return; }
        try
        {
            clipboard?.Copy(entry.Text);
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't copy the dictation."));
        }
    }

    /// <summary>A store operation, or <c>null</c> when the disk said no. Counts only ever reach the log.</summary>
    private static IReadOnlyList<HistoryEntry>? TryHistory(
        Func<IReadOnlyList<HistoryEntry>> operation,
        string failureEvent = "history.write.failed")
    {
        try
        {
            return operation();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.EmitException(failureEvent, error);
            return null;
        }
    }
}
