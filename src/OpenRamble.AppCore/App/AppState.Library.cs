using System.Runtime.InteropServices;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>What <see cref="AppState.TrashRecordings"/> did.</summary>
public enum RecordingTrashOutcome
{
    /// <summary>In the Trash (or there was nothing to move).</summary>
    Trashed,

    /// <summary>
    /// Nothing was touched: this desktop has no Trash. The window asks, and on yes calls
    /// <see cref="AppState.DeleteRecordingsPermanently"/>.
    /// </summary>
    NeedsPermanentDeletion,

    /// <summary>The move failed part way; a notice said so, and the list shows what is left.</summary>
    Failed,
}

/// <summary>
/// The Library: dictations and recordings in one list, and files brought in to be transcribed
/// (upstream AppState.swift, "Recordings" and "Imported files").
/// </summary>
/// <remarks>
/// Importer callbacks arrive on background threads; like everything else here, state is written
/// under <c>gate</c> and <see cref="PropertyChanged"/> is raised on that thread for the binding
/// layer to marshal. Never call into the importer while holding <c>gate</c>: it asks
/// <see cref="IsEngineReady"/> under its own lock.
/// </remarks>
public sealed partial class AppState
{
    private MeetingStore? meetingStore;
    private RecordingImporter? importer;
    private IReadOnlyList<MeetingRecordingMetadata> recordings = [];
    private Guid? lastFinishedRecordingId;
    private readonly Dictionary<Guid, IReadOnlyList<MeetingUtterance>> loadedTranscripts = [];

    /// <summary>
    /// Read without <c>gate</c> (the idle-unload check reads it under <c>gate</c>), published
    /// under <see cref="importActivityGate"/> so two callbacks on two threads cannot leave an older
    /// activity in place of a newer one.
    /// </summary>
    private volatile RecordingImportActivity importActivity = new();
    private readonly Lock importActivityGate = new();

    /// <summary>Every published recording, newest first.</summary>
    public IReadOnlyList<MeetingRecordingMetadata> Recordings
    {
        get { lock (gate) { return recordings; } }
    }

    /// <summary>What the importer is doing: files being read in, the one transcribing, the queue.</summary>
    public RecordingImportActivity ImportActivity => importActivity;

    /// <summary>
    /// The recording the Library window should select: the file just brought in (of several, the
    /// first - its words are the ones arriving).
    /// </summary>
    public Guid? LastFinishedRecordingId
    {
        get { lock (gate) { return lastFinishedRecordingId; } }
        private set => Set(ref lastFinishedRecordingId, value);
    }

    /// <summary>
    /// A recording's transcript changed - a piece of an import was read, or it finished. Raised
    /// on a background thread; read it again with <see cref="Transcript"/>.
    /// </summary>
    public event Action<Guid>? TranscriptChanged;

    /// <summary>Whether files can be brought in at all (an engine to read them with).</summary>
    public bool CanImport => importer is not null;

    private void SetUpLibrary(AppEnvironment environment)
    {
        try
        {
            meetingStore = new MeetingStore(paths.Recordings(), environment.RecordingTrasher);
            // Before anything writes: a crash's leftovers are repaired and published.
            meetingStore.RecoverIncomplete(now());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.EmitException("library.setup.failed", error);
            meetingStore = null;
            return;
        }

        ReloadRecordings();
        if (environment.ImportRecognizer is not { } recognizer) { return; }

        var made = new RecordingImporter(new RecordingImporter.Edges(
            meetingStore,
            recognizer,
            () => IsEngineReady && ModelStateValue.IsReady,
            // Fillers go and the dictionary applies, as in a recording; no spoken corrections in
            // somebody else's speech.
            () => new TextPipeline(
                [.. StarterDictionary.Missing(Replacements), .. Replacements],
                spokenCorrections: false))
        {
            WaitForTurn = dictationPriority is { } priority ? priority.WaitForTurn : static _ => Task.CompletedTask,
            Compressor = environment.RecordingCompressor,
        });
        made.OnActivity = _ => PublishImportActivity();
        made.OnImported = HandleImported;
        made.OnTranscript = (id, utterances) =>
        {
            lock (gate) { loadedTranscripts[id] = utterances; }
            TranscriptChanged?.Invoke(id);
        };
        made.OnFinished = id =>
        {
            // Read again from disk: what was written last is the whole transcript.
            lock (gate) { loadedTranscripts.Remove(id); }
            ReloadRecordings();
            TranscriptChanged?.Invoke(id);
        };
        made.OnFailure = HandleImportFailure;
        importer = made;
        // Whatever a quit interrupted. It waits here until the model is ready.
        made.Resume(meetingStore.List());
    }

    /// <summary>
    /// Imports run only on a ready engine and model: either changing is the importer's cue. Off
    /// this thread - the importer asks for readiness under its own lock, and a setter may run
    /// under ours.
    /// </summary>
    private void ImportReadinessChanged()
    {
        if (importer is { } waiting) { _ = Task.Run(waiting.ModelsChanged); }
    }

    /// <summary>Taken before a listing starts; the newest listing shown so far.</summary>
    private long recordingsListingStarted;
    private long recordingsListingShown;

    /// <summary>Test-only: runs between a listing and its showing, so a test can hold one there.</summary>
    public Action? AfterRecordingsListed { get; set; }

    /// <summary>
    /// Read the list from disk again. Listings run on many threads at once; one that started
    /// earlier never replaces one that started later, so a list read before a trash cannot bring
    /// the trashed recording back.
    /// </summary>
    public void ReloadRecordings()
    {
        if (meetingStore is null) { return; }
        long generation = Interlocked.Increment(ref recordingsListingStarted);
        IReadOnlyList<MeetingRecordingMetadata> listed = meetingStore.List();
        AfterRecordingsListed?.Invoke();
        lock (gate)
        {
            // Checked and assigned in one step, so a newer listing cannot slip in between.
            if (generation < recordingsListingShown) { return; }
            recordingsListingShown = generation;
            recordings = listed;
        }

        Raise(nameof(Recordings));
    }

    public string? RecordingAudioPath(Guid id) => meetingStore?.AudioPath(id);

    public string? RecordingPeaksPath(Guid id) => meetingStore?.PeaksPath(id);

    // MARK: - Importing

    /// <summary>
    /// Bring audio or video files into the Library and transcribe them. Each becomes a recording
    /// at once, with its sound; its words follow as they are read. Dictation goes first throughout.
    /// </summary>
    /// <returns>The copying, for a caller (or a test) that wants to await it.</returns>
    public Task ImportFiles(IReadOnlyList<string> files)
    {
        if (importer is null || files.Count == 0) { return Task.CompletedTask; }
        // The importer is busy from here on, before the engine is asked: an idle unload under
        // way either sees the activity and rewarms, or this sees it unloaded.
        Task copying = importer.Add(files);
        // An engine resting after an hour idle comes back for the file, as it does for a key press.
        RewarmEngineIfCold(RewarmTrigger.Import);
        return copying;
    }

    private void PublishImportActivity()
    {
        if (importer is not { } source) { return; }
        bool changed;
        lock (importActivityGate)
        {
            RecordingImportActivity current = source.Activity;
            changed = !ReferenceEquals(current, importActivity);
            importActivity = current;
        }

        if (!changed) { return; }
        Raise(nameof(ImportActivity));
        // Idle again: the countdown may start. Busy: it is held off.
        RescheduleIdleUnload();
    }

    private void HandleImported(MeetingRecordingMetadata metadata)
    {
        // Raised before the file joins the queue: an importer with nothing else on its hands
        // means this is the first of the batch.
        RecordingImportActivity activity = importer?.Activity ?? new RecordingImportActivity();
        bool first = activity.Transcribing is null && activity.Waiting.Count == 0;
        lock (gate) { loadedTranscripts[metadata.Id] = []; }
        ReloadRecordings();
        if (first) { LastFinishedRecordingId = metadata.Id; }
    }

    private void HandleImportFailure(RecordingImporter.Failure failure, string name)
    {
        string message = failure switch
        {
            RecordingImporter.Failure.Unreadable =>
                $"Couldn't read “{name}”. OpenRamble reads audio and video such as M4A, MP3, WAV, MP4 and MOV.",
            RecordingImporter.Failure.Empty => $"“{name}” has no sound in it.",
            _ => $"Couldn't add “{name}” to the Library.",
        };
        // A file's name is the person's own: the log gets the kind of failure only.
        Notify(new DictationNotice(DictationNoticeKind.Failure, message, wordsDidNotLand: false)
        {
            LogMessage = $"recording import failed: {failure}",
        });
    }

    // MARK: - Transcripts

    /// <summary>
    /// The transcript of a recording: from memory while an import is writing it, else read from
    /// disk the first time it is asked for and kept.
    /// </summary>
    public IReadOnlyList<MeetingUtterance> Transcript(Guid id)
    {
        lock (gate)
        {
            if (loadedTranscripts.TryGetValue(id, out IReadOnlyList<MeetingUtterance>? loaded)) { return loaded; }
        }

        IReadOnlyList<MeetingUtterance> read = meetingStore?.Transcript(id)?.Utterances ?? [];
        lock (gate)
        {
            // An import may have written a newer one meanwhile; it wins.
            if (loadedTranscripts.TryGetValue(id, out IReadOnlyList<MeetingUtterance>? newer)) { return newer; }
            loadedTranscripts[id] = read;
            return read;
        }
    }

    /// <summary>
    /// The Library list: dictations and recordings by day, newest first, filtered by scope and
    /// search. The search reads recordings' transcripts too (a deliberate deviation).
    /// </summary>
    public IReadOnlyList<LibraryDay> LibraryDays(LibraryScope scope = LibraryScope.All, string query = "")
    {
        bool searching = query.Trim().Length > 0;
        IEnumerable<ILibraryRecording> listed = Recordings.Select(recording => new LibraryRecording(
            recording,
            searching ? string.Join(" ", Transcript(recording.Id).Select(utterance => utterance.Text)) : ""));
        return LibraryIndex.Days(History, listed, scope, query);
    }

    /// <summary>
    /// Host-only, like every copy this app makes. An imported file has no speaker names: who
    /// speaks in it is not something the app knows.
    /// </summary>
    public void CopyTranscript(Guid id)
    {
        IReadOnlyList<MeetingUtterance> utterances = Transcript(id);
        if (utterances.Count == 0) { return; }
        bool imported = Recordings.FirstOrDefault(recording => recording.Id == id)?.IsImported ?? false;
        try
        {
            clipboard?.Copy(MeetingTranscriptFormatter.PlainText(utterances, speakers: !imported));
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't copy the transcript.", wordsDidNotLand: false));
        }
    }

    // MARK: - Managing recordings

    /// <summary>Retitle; a blank title means none.</summary>
    public void RenameRecording(Guid id, string? title)
    {
        if (meetingStore is null) { return; }
        try
        {
            meetingStore.Rename(id, title);
            ReloadRecordings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't rename the recording.", wordsDidNotLand: false));
        }
    }

    /// <summary>
    /// To the desktop's Trash, where it can come back from. A file still being transcribed stops
    /// being transcribed. Where there is no Trash nothing is touched and the caller is told to ask.
    /// </summary>
    public RecordingTrashOutcome TrashRecordings(IReadOnlyCollection<Guid> ids)
    {
        if (meetingStore is null || ids.Count == 0) { return RecordingTrashOutcome.Trashed; }
        if (!meetingStore.CanTrash) { return RecordingTrashOutcome.NeedsPermanentDeletion; }
        return RemoveRecordings(ids, meetingStore.Trash, "Couldn't move the recording to the Trash.")
            ? RecordingTrashOutcome.Trashed
            : RecordingTrashOutcome.Failed;
    }

    /// <summary>
    /// <see cref="TrashRecordings"/> off the calling thread: the Trash is a process per
    /// recording, with a timeout of its own, and the window must not freeze meanwhile.
    /// </summary>
    public Task<RecordingTrashOutcome> TrashRecordingsAsync(IReadOnlyCollection<Guid> ids)
    {
        Guid[] copy = [.. ids];
        return Task.Run(() => TrashRecordings(copy));
    }

    /// <summary>Gone for good - only after the person agreed to, on a desktop without a Trash.</summary>
    public void DeleteRecordingsPermanently(IReadOnlyCollection<Guid> ids)
    {
        if (meetingStore is null || ids.Count == 0) { return; }
        RemoveRecordings(ids, meetingStore.DeletePermanently, "Couldn't delete the recording.");
    }

    /// <summary>One library operation: the list is read again once, not after every row.</summary>
    private bool RemoveRecordings(IReadOnlyCollection<Guid> ids, Action<Guid> remove, string failure)
    {
        bool removedAll = true;
        foreach (Guid id in ids)
        {
            try
            {
                remove(id);
                // Only once it is gone: a recording the Trash refused keeps its transcript coming.
                importer?.Cancel(id);
                lock (gate) { loadedTranscripts.Remove(id); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.EmitException("library.remove.failed", error);
                removedAll = false;
            }
        }

        ReloadRecordings();
        if (!removedAll)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, failure, wordsDidNotLand: false));
        }

        return removedAll;
    }

    /// <summary>Dictations are deleted for good, their audio too - as in History.</summary>
    public void DeleteDictations(IReadOnlyCollection<Guid> ids)
    {
        if (historyStore is null || ids.Count == 0) { return; }
        IReadOnlyList<HistoryEntry> remaining = History;
        foreach (HistoryEntry entry in History.Where(entry => ids.Contains(entry.Id)))
        {
            remaining = TryHistory(() => historyStore.Delete(entry)) ?? remaining;
        }

        History = remaining;
        // Deleted means gone from every place this app shows the words, the menu included.
        RecentDictations = [.. RecentDictations.Where(take => take.HistoryId is not { } id || !ids.Contains(id))];
    }

    /// <summary>Open the recording's folder in the file manager.</summary>
    public void RevealRecording(Guid id)
    {
        if (meetingStore is null || !meetingStore.IsPublished(id)) { return; }
        try
        {
            folders?.Open(meetingStore.Directory(id));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't open the recording's folder.", wordsDidNotLand: false));
        }
    }
}
