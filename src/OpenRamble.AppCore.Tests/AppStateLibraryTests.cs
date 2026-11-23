using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The Library over a real <see cref="AppState"/>: a file brought in is listed, shown,
/// transcribed on the dictation engine and archived; trashed, it stops; a quit loses nothing.
/// </summary>
/// <remarks>
/// Ported from upstream AppStateImportTests.swift (the Hebrew cases are not: always automatic
/// here) and the rename/trash case of AppStateRecordingTests.swift.
/// </remarks>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateLibraryTests : IDisposable
{
    private readonly AppHarness harness = new();
    private readonly string files;

    public AppStateLibraryTests()
    {
        files = Path.Combine(harness.Root, "import-files");
        Directory.CreateDirectory(files);
    }

    public void Dispose() => harness.Dispose();

    private string Speech(string name, int seconds = 5)
    {
        string path = Path.Combine(files, name);
        SpeechLikeAudio.Write(path, seconds);
        return path;
    }

    private async Task<AppState> ReadyState()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        return state;
    }

    private static Task Complete(AppState state) =>
        TestWait.For(
            "the transcript",
            () => state.ImportActivity.IsIdle
                && state.Recordings.Count > 0
                && state.Recordings.All(recording => recording.TranscriptionState == MeetingTranscriptionState.Complete),
            timeoutMilliseconds: 15_000);

    [Fact]
    public async Task AnImportedFileIsListedShownTranscribedAndArchived()
    {
        AppState state = await ReadyState();
        harness.ImportEngine.HoldNextDecode();

        _ = state.ImportFiles([Speech("Interview with Dana.m4a.wav")]);
        await TestWait.For("the first decode", () => harness.ImportEngine.IsDecodeWaiting);

        // Its sound is in the Library before a word of it is.
        MeetingRecordingMetadata recording = Assert.Single(state.Recordings);
        Assert.Equal("Interview with Dana.m4a", recording.Title);
        Assert.Equal(recording.Id, state.LastFinishedRecordingId);
        Assert.Equal(recording.Id, state.ImportActivity.Transcribing);
        Assert.Empty(state.Transcript(recording.Id));
        Assert.NotNull(state.RecordingAudioPath(recording.Id));

        harness.ImportEngine.ReleaseDecode();
        await Complete(state);

        Assert.Equal(["Piece 1."], state.Transcript(recording.Id).Select(utterance => utterance.Text));
        // Whoever speaks in a file, it is not "You": the text leaves with its times, no names.
        state.CopyTranscript(recording.Id);
        string copied = Assert.Single(harness.Clipboard.Copied);
        Assert.Equal("00:00:00\nPiece 1.", copied);
        // A finished import keeps the compact copy of its sound.
        await TestWait.For(
            "the compact archive",
            () => Path.GetFileName(state.RecordingAudioPath(recording.Id)) == MeetingStore.CompressedAudioFileName);
    }

    /// <summary>Of several files the window shows the first: its words are the ones arriving.</summary>
    [Fact]
    public async Task OfSeveralFilesTheFirstIsTheOneShown()
    {
        AppState state = await ReadyState();
        harness.ImportEngine.HoldNextDecode();

        _ = state.ImportFiles([Speech("First.wav"), Speech("Second.wav"), Speech("Third.wav")]);
        await TestWait.For("three files in the Library", () => state.Recordings.Count == 3);
        await TestWait.For("the first decode", () => harness.ImportEngine.IsDecodeWaiting);

        MeetingRecordingMetadata first = state.Recordings.Single(recording => recording.Title == "First");
        Assert.Equal(first.Id, state.LastFinishedRecordingId);
        Assert.Equal(first.Id, state.ImportActivity.Transcribing);
        Assert.Equal(2, state.ImportActivity.Waiting.Count);

        harness.ImportEngine.ReleaseDecode();
        await Complete(state);
    }

    [Fact]
    public async Task AFileBroughtInBeforeTheModelWaitsForIt()
    {
        AppState state = harness.MakeState();
        await state.RefreshModelState();

        _ = state.ImportFiles([Speech("Early.wav")]);
        await TestWait.For("the file in the Library", () => state.Recordings.Count == 1);
        await TestWait.For("the wait for a model", () => state.ImportActivity.NeedsModel);
        Guid id = state.Recordings[0].Id;
        Assert.Equal([id], state.ImportActivity.Waiting);
        Assert.Equal(
            "Waiting for the speech model.",
            RecordingsPlaceholder.ImportLine(id, state.ImportActivity, isDictating: false));

        harness.InstallModelMarker();
        await state.RefreshModelState();
        await Complete(state);
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsSaidAndItsNameStaysOutOfTheLog()
    {
        AppState state = await ReadyState();
        string file = Path.Combine(files, "Private Client Call.m4a");
        await File.WriteAllTextAsync(file, "not audio");

        _ = state.ImportFiles([file]);
        await TestWait.For("the notice", () => state.LastNotice?.Kind == DictationNoticeKind.Failure);

        Assert.Equal(
            "Couldn't read “Private Client Call”. OpenRamble reads audio and video such as M4A, MP3, WAV, MP4 and MOV.",
            state.LastNotice!.Message);
        Assert.DoesNotContain("Private", state.LastNotice.LogMessage, StringComparison.Ordinal);
        Assert.Empty(state.Recordings);
    }

    [Fact]
    public async Task MovingAnImportToTheTrashStopsIt()
    {
        AppState state = await ReadyState();
        harness.ImportEngine.HoldNextDecode();
        _ = state.ImportFiles([Speech("Mistake.wav", seconds: 100)]);
        await TestWait.For("the first decode", () => harness.ImportEngine.IsDecodeWaiting);
        Guid id = state.Recordings[0].Id;

        Assert.Equal(RecordingTrashOutcome.Trashed, state.TrashRecordings([id]));
        harness.ImportEngine.ReleaseDecode();
        await TestWait.For("the importer to rest", () => state.ImportActivity.IsIdle);

        Assert.Empty(state.Recordings);
        Assert.Equal(1, harness.ImportEngine.Decodes);
    }

    [Fact]
    public async Task AnImportAQuitInterruptedIsFinishedAtTheNextLaunch()
    {
        AppState first = await ReadyState();
        harness.ImportEngine.HoldNextDecode();
        _ = first.ImportFiles([Speech("Lecture.wav")]);
        await TestWait.For("the first decode", () => harness.ImportEngine.IsDecodeWaiting);
        Guid id = first.Recordings[0].Id;

        // The quit: the decode under way ends with nobody to hear it.
        first.Dispose();
        harness.ImportEngine.ReleaseDecode();
        await Task.Delay(80);
        var store = new MeetingStore(harness.Paths.Recordings(), harness.RecordingTrasher);
        Assert.Equal(MeetingTranscriptionState.Live, store.Metadata(id)!.TranscriptionState);

        AppState second = harness.MakeState();
        await second.RefreshModelState();
        await Complete(second);
        Assert.Equal([id], second.Recordings.Select(recording => recording.Id));
        Assert.NotEmpty(second.Transcript(id));
    }

    [Fact]
    public async Task RenameAndTrash()
    {
        AppState state = await ReadyState();
        _ = state.ImportFiles([Speech("Note.wav")]);
        await Complete(state);
        Guid id = state.Recordings[0].Id;

        state.RenameRecording(id, "  Budget review ");
        Assert.Equal("Budget review", state.Recordings[0].Title);
        state.RenameRecording(id, " ");
        Assert.Null(state.Recordings[0].Title);

        Assert.Equal(RecordingTrashOutcome.Trashed, state.TrashRecordings([id]));
        Assert.Empty(state.Recordings);
    }

    /// <summary>The Trash refused: the recording stays, and so does its transcript coming in.</summary>
    [Fact]
    public async Task ATrashThatFailsKeepsTheImportGoing()
    {
        AppState state = await ReadyState();
        harness.ImportEngine.HoldNextDecode();
        _ = state.ImportFiles([Speech("Keep.wav")]);
        await TestWait.For("the first decode", () => harness.ImportEngine.IsDecodeWaiting);
        Guid id = state.Recordings[0].Id;

        harness.RecordingTrasher.Failure = new IOException("refused");
        Assert.Equal(RecordingTrashOutcome.Failed, await state.TrashRecordingsAsync([id]));
        Assert.Equal(id, state.ImportActivity.Transcribing);

        harness.ImportEngine.ReleaseDecode();
        await Complete(state);
        Assert.Equal([id], state.Recordings.Select(recording => recording.Id));
    }

    /// <summary>
    /// Listings run on many threads (importer callbacks, the window); one read before a trash and
    /// shown after it must not bring the trashed recording back.
    /// </summary>
    [Fact]
    public async Task AStaleListingCannotBringBackATrashedRecording()
    {
        var store = new MeetingStore(harness.Paths.Recordings(), harness.RecordingTrasher);
        MeetingRecordingMetadata recording = MeetingRecordingMetadata.Create(
            startedAt: DateTimeOffset.Now,
            systemAudio: new SystemAudioSummary(wasRequested: false));
        store.Write(recording);
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.Single(state.Recordings);

        using var listed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        state.AfterRecordingsListed = () =>
        {
            state.AfterRecordingsListed = null;
            listed.Set();
            release.Wait();
        };
        Task stale = Task.Run(state.ReloadRecordings);
        listed.Wait();

        Assert.Equal(RecordingTrashOutcome.Trashed, state.TrashRecordings([recording.Id]));
        Assert.Empty(state.Recordings);
        release.Set();
        await stale;

        Assert.Empty(state.Recordings);
    }

    /// <summary>A desktop without a Trash touches nothing until the person agrees to delete for good.</summary>
    [Fact]
    public async Task WithoutATrashDeletingWaitsForConsent()
    {
        harness.RecordingTrasher.IsAvailable = false;
        AppState state = await ReadyState();
        _ = state.ImportFiles([Speech("Note.wav")]);
        await Complete(state);
        Guid id = state.Recordings[0].Id;

        Assert.Equal(RecordingTrashOutcome.NeedsPermanentDeletion, state.TrashRecordings([id]));
        Assert.Single(state.Recordings);

        state.DeleteRecordingsPermanently([id]);
        Assert.Empty(state.Recordings);
    }

    /// <summary>The search finds a recording by the words of its transcript.</summary>
    [Fact]
    public async Task TheLibrarySearchReadsTranscripts()
    {
        AppState state = await ReadyState();
        _ = state.ImportFiles([Speech("Note.wav")]);
        await Complete(state);
        Guid id = state.Recordings[0].Id;

        LibraryItem found = Assert.Single(Assert.Single(state.LibraryDays(LibraryScope.All, "piece")).Items);
        Assert.Equal(id, found.Id);
        Assert.Empty(state.LibraryDays(LibraryScope.All, "nowhere"));
        Assert.Empty(state.LibraryDays(LibraryScope.Dictations));
    }

    /// <summary>The engine an import reads with is not given back for idleness while it reads.</summary>
    [Fact]
    public async Task AnImportHoldsOffTheIdleUnload()
    {
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });
        harness.WarmUpEngine = new CountingASREngine();
        AppState state = await ReadyState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);
        harness.ImportEngine.HoldNextDecode();
        _ = state.ImportFiles([Speech("Long.wav")]);
        await TestWait.For("the first decode", () => harness.ImportEngine.IsDecodeWaiting);

        state.IdleUnloadDelayOverride = TimeSpan.FromMilliseconds(20);
        Assert.False(state.IsAwaitingIdleUnload);
        await Task.Delay(100);
        Assert.True(state.IsEngineReady);

        harness.ImportEngine.ReleaseDecode();
        await TestWait.For("the countdown after the import", () => !state.IsEngineReady);
    }

    [Fact]
    public async Task DeletedDictationsLeaveTheLibrary()
    {
        AppState state = await ReadyState();
        harness.Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        harness.Monitor.OnRelease!();
        await TestWait.For("the take was archived", () => state.History.Count == 1);
        Assert.Single(Assert.Single(state.LibraryDays(LibraryScope.Dictations)).Items);

        state.DeleteDictations([state.History[0].Id]);

        Assert.Empty(state.History);
        Assert.Empty(state.LibraryDays());
    }
}
