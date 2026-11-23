using DictationCore;
using DictationCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Start, the live state, stop, filing - the whole loop with no microphone. Ported from upstream
/// AppStateRecordingTests.swift; the last cases (transcript, quit, a failure that stops) are the
/// port's own, over the same fakes.
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateRecordingTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    private static Task WaitFor(string what, Func<bool> condition, int timeoutMilliseconds = 5_000) =>
        TestWait.For(what, condition, timeoutMilliseconds);

    private static async Task<MeetingRecordingMetadata> RecordOne(AppState state)
    {
        await state.StartRecording(includeSystemAudio: false);
        Assert.Equal(MeetingRecordingState.Recording, state.RecordingState);
        await state.StopRecording();
        await WaitFor("the filed recording", () => state.RecordingState == MeetingRecordingState.Idle && state.Recordings.Count > 0);
        return state.Recordings[0];
    }

    [Fact]
    public async Task ARecordingStartsIsFiledWhenStoppedAndListed()
    {
        AppState state = harness.MakeState();
        Assert.True(state.CanRecord);
        Assert.Equal(MeetingRecordingState.Idle, state.RecordingState);
        Assert.Empty(state.Recordings);

        await state.StartRecording(includeSystemAudio: false);
        Assert.Equal(MeetingRecordingState.Recording, state.RecordingState);
        Assert.True(state.IsRecording);
        Assert.Equal(1, harness.MeetingCapture.StartCount);
        Assert.False(harness.MeetingCapture.IncludesSystemAudio);
        MeetingRecordingMetadata live = Assert.IsType<MeetingRecordingMetadata>(state.LiveRecording);
        Assert.Equal(live.Id, state.LiveRecordingId);
        string directory = harness.MeetingCapture.Directory;
        Assert.Equal(live.Id.ToString(), Path.GetFileName(directory));
        Assert.Equal(".incomplete", Path.GetFileName(Path.GetDirectoryName(directory)));

        await state.StopRecording();
        Assert.Equal(MeetingRecordingState.Idle, state.RecordingState);
        Assert.Equal(1, harness.MeetingCapture.StopCount);
        Assert.Null(state.LiveRecording);
        MeetingRecordingMetadata filed = Assert.Single(state.Recordings);
        Assert.Equal(live.Id, filed.Id);
        Assert.Equal(2, filed.Duration);
        Assert.Equal(MeetingEndReason.StoppedByUser, filed.EndReason);
        Assert.Equal("Fake Microphone", filed.MicrophoneDeviceName);
        Assert.Equal(live.Id, state.LastFinishedRecordingId);
        Assert.True(Directory.Exists(Path.Combine(harness.Paths.Recordings(), live.Id.ToString())), "moved out of .incomplete");
    }

    [Fact]
    public async Task WithoutMicrophonePermissionNothingStartsAndThePersonIsAsked()
    {
        harness.Permissions.MicrophoneGranted = false;
        AppState state = harness.MakeState();
        await state.StartRecording();
        Assert.Equal(MeetingRecordingState.Idle, state.RecordingState);
        Assert.Equal(0, harness.MeetingCapture.StartCount);
        Assert.Equal(DictationNoticeKind.Warning, state.LastNotice?.Kind);
    }

    [Fact]
    public async Task AStartFailureLeavesNothingBehindAndSaysWhy()
    {
        harness.MeetingCapture.StartError = new MeetingCapture.Failure.DiskFull();
        AppState state = harness.MakeState();
        await state.StartRecording();
        Assert.Equal(MeetingRecordingState.Idle, state.RecordingState);
        Assert.Null(state.LiveRecording);
        Assert.Equal(DictationNoticeKind.Failure, state.LastNotice?.Kind);
        Assert.Contains("space", state.LastNotice?.Message);
        string incomplete = Path.Combine(harness.Paths.Recordings(), ".incomplete");
        Assert.Empty(Directory.Exists(incomplete) ? Directory.GetFileSystemEntries(incomplete) : []);
    }

    [Fact]
    public async Task PauseAndResumeReachTheRecorder()
    {
        AppState state = harness.MakeState();
        await state.StartRecording();
        await state.PauseRecording();
        Assert.Equal(MeetingRecordingState.Paused, state.RecordingState);
        Assert.True(state.IsRecording);
        Assert.Equal(1, harness.MeetingCapture.PauseCount);
        await state.ResumeRecording();
        Assert.Equal(MeetingRecordingState.Recording, state.RecordingState);
        Assert.Equal(1, harness.MeetingCapture.ResumeCount);
        await state.StopRecording();
        Assert.Equal(MeetingRecordingState.Idle, state.RecordingState);
    }

    [Fact]
    public async Task AFullDiskEndsTheRecordingWithTheReasonKept()
    {
        harness.MeetingCapture.EndReason = MeetingEndReason.DiskFull;
        AppState state = harness.MakeState();
        MeetingRecordingMetadata filed = await RecordOne(state);
        Assert.Equal(MeetingEndReason.DiskFull, filed.EndReason);
        Assert.Equal(DictationNoticeKind.Warning, state.LastNotice?.Kind);
        Assert.Contains("kept", state.LastNotice?.Message);
    }

    [Fact]
    public async Task RenameAndTrash()
    {
        AppState state = harness.MakeState();
        MeetingRecordingMetadata filed = await RecordOne(state);
        state.RenameRecording(filed.Id, "Budget review");
        Assert.Equal("Budget review", state.Recordings[0].Title);
        Assert.Equal(RecordingTrashOutcome.Trashed, state.TrashRecordings([filed.Id]));
        Assert.Empty(state.Recordings);
    }

    [Fact]
    public void ARecordingLeftByACrashIsRecoveredAndDisclosedAtLaunch()
    {
        var store = new MeetingStore(harness.Paths.Recordings());
        Guid id = Guid.NewGuid();
        string directory = store.IncompleteDirectory(id);
        Directory.CreateDirectory(directory);
        var writer = new MeetingWriter(directory);
        writer.Open();
        writer.Append(Enumerable.Repeat(0.2f, 16_000).ToArray(), new float[16_000]);
        writer.Abandon();
        // Old enough not to look like another instance's live file.
        File.SetLastWriteTimeUtc(Path.Combine(directory, MeetingWriter.AudioFileName), DateTime.UtcNow.AddMinutes(-2));

        AppState state = harness.MakeState();
        Assert.Equal([id], state.Recordings.Select(recording => recording.Id));
        Assert.Equal(MeetingEndReason.CrashRecovered, state.Recordings[0].EndReason);
        Assert.Equal(1, state.Recordings[0].Duration);
        Assert.Equal("A recording was recovered after an interruption.", state.LastNotice?.Message);
        Assert.NotNull(state.RecordingAudioPath(id));
    }

    /// <summary>
    /// A segment handed out during the recording is decoded on the shared engine and shown live;
    /// after the stop the transcript is closed, marked complete, and the WAV archived.
    /// </summary>
    [Fact]
    public async Task SegmentsAreTranscribedLiveThenCompletedAndArchived()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        var changed = new List<Guid>();
        state.TranscriptChanged += id => { lock (changed) { changed.Add(id); } };

        await state.StartRecording(includeSystemAudio: false);
        Guid id = state.LiveRecordingId!.Value;
        Assert.Equal(id, state.TranscribingRecordingId);
        harness.MeetingCapture.OnSegment!(new MeetingSegmentRef(MeetingChannel.Microphone, 0, 16_000));
        await WaitFor("the live text", () => state.Transcript(id).Count == 1);
        Assert.Equal("Piece 1.", state.Transcript(id)[0].Text);
        Assert.Equal(16_000, Assert.Single(harness.ImportEngine.DecodedSamples));
        lock (changed) { Assert.Contains(id, changed); }

        await state.StopRecording();
        await WaitFor(
            "the complete transcript",
            () => state.Recordings.FirstOrDefault()?.TranscriptionState == MeetingTranscriptionState.Complete
                && state.TranscribingRecordingId is null,
            timeoutMilliseconds: 10_000);
        await WaitFor(
            "the compact archive",
            () => Path.GetFileName(state.RecordingAudioPath(id)) == MeetingStore.CompressedAudioFileName);
        Assert.Equal(["Piece 1."], state.Transcript(id).Select(utterance => utterance.Text));
        MeetingTranscript? onDisk = new MeetingStore(harness.Paths.Recordings()).Transcript(id);
        Assert.Equal(16_000, onDisk?.DecodedFrames[MeetingChannel.Microphone]);
    }

    [Fact]
    public async Task AFailureThatStopsTheRecorderFilesWhatReachedDisk()
    {
        harness.MeetingCapture.EndReason = MeetingEndReason.WriteFailed;
        AppState state = harness.MakeState();
        await state.StartRecording();
        harness.MeetingCapture.OnFailure!(new MeetingCapture.Failure.WriteFailed("gone"));
        await WaitFor("the filed recording", () => state.RecordingState == MeetingRecordingState.Idle && state.Recordings.Count == 1);
        Assert.Equal(MeetingEndReason.WriteFailed, state.Recordings[0].EndReason);
        await WaitFor("the notice", () => state.LastNotice?.Message.Contains("could no longer be written") == true);
    }

    [Fact]
    public async Task AQuitFilesTheRecordingAsEndedByTheQuit()
    {
        AppState state = harness.MakeState();
        await state.StartRecording();
        await state.StopRecordingBeforeQuit();
        Assert.Equal(MeetingEndReason.ApplicationQuit, Assert.Single(state.Recordings).EndReason);
        Assert.True(Assert.Single(state.Recordings).SystemAudio.WasRequested, "a meeting by default");
    }

    [Fact]
    public async Task AMeetingWhereTheBuildCannotHearTheComputerRecordsTheMicrophoneOnly()
    {
        harness.SupportsSystemAudio = false;
        AppState state = harness.MakeState();
        await state.StartRecording();
        Assert.False(harness.MeetingCapture.IncludesSystemAudio);
        Assert.IsType<CaptureHealth.NotRequested>(state.LiveCaptureHealth);
        await state.StopRecording();
    }
}
