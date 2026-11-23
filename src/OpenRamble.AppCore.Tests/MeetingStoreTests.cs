using System.Buffers.Binary;
using DictationCore;

namespace OpenRamble.AppCore.Tests;

public sealed class MeetingStoreTests : IDisposable
{
    private readonly string root;
    private readonly RecordingTrasher trasher = new();
    private readonly MeetingStore store;

    public MeetingStoreTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"meeting-store-{Guid.NewGuid()}");
        Directory.CreateDirectory(root);
        store = new MeetingStore(root, trasher);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class RecordingTrasher : IRecordingTrasher
    {
        public List<string> Trashed { get; } = [];

        public bool IsAvailable { get; set; } = true;

        public void Trash(string path)
        {
            Trashed.Add(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static MeetingRecordingMetadata Metadata(DateTimeOffset? startedAt = null, string? title = null) =>
        MeetingRecordingMetadata.Create(
            startedAt: startedAt ?? DateTimeOffset.Now,
            duration: 12,
            title: title,
            systemAudio: new SystemAudioSummary(wasRequested: false),
            endReason: MeetingEndReason.StoppedByUser);

    /// <summary>
    /// A recording as the capture leaves it: audio and peaks in the incomplete directory, header
    /// sealed or not. Upstream drives MeetingWriter here; until that port lands (wave 4a task B)
    /// the fixture writes the same stereo 16-bit layout by hand.
    /// </summary>
    private void WriteAudio(Guid id, int frames, bool sealed_)
    {
        string directory = store.IncompleteDirectory(id);
        Directory.CreateDirectory(directory);
        int dataBytes = frames * 4;
        byte[] file = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(file.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), sealed_ ? (uint)(36 + dataBytes) : 0);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 16_000 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), sealed_ ? (uint)dataBytes : 0);
        for (int frame = 0; frame < frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(44 + (frame * 4)), 16_384);
        }

        File.WriteAllBytes(Path.Combine(directory, MeetingStore.AudioFileName), file);
        File.WriteAllBytes(Path.Combine(directory, MeetingStore.PeaksFileName), new byte[16]);
    }

    private static uint DeclaredDataBytes(string wav)
    {
        byte[] head = File.ReadAllBytes(wav).AsSpan(0, 44).ToArray();
        return BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(40));
    }

    [Fact]
    public void AnEmptyRootListsNothingAndOccupiesNothing()
    {
        Assert.Empty(store.List());
    }

    [Fact]
    public void APublishedRecordingIsListedNewestFirst()
    {
        MeetingRecordingMetadata older = Metadata(DateTimeOffset.FromUnixTimeSeconds(1_000));
        MeetingRecordingMetadata newer = Metadata(DateTimeOffset.FromUnixTimeSeconds(2_000));
        store.Write(older);
        store.Write(newer);
        Assert.Equal([newer.Id, older.Id], store.List().Select(m => m.Id));
    }

    [Fact]
    public void CompressedAudioIsUsedAfterTheRawSourceIsCompacted()
    {
        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording);
        string compressed = Path.Combine(store.Directory(recording.Id), MeetingStore.CompressedAudioFileName);
        File.WriteAllBytes(compressed, [0, 1, 2]);

        Assert.Equal(compressed, store.AudioPath(recording.Id));
        Assert.Equal(compressed, store.CompressedAudioPath(recording.Id));
        Assert.Null(store.RawAudioPath(recording.Id));
    }

    [Fact]
    public void AnIncompleteRecordingIsNotListedUntilPublished()
    {
        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording, incomplete: true);
        WriteAudio(recording.Id, frames: 1_600, sealed_: true);
        Assert.Empty(store.List());
        Assert.Null(store.AudioPath(recording.Id));

        store.Publish(recording.Id);
        Assert.Equal([recording.Id], store.List().Select(m => m.Id));
        Assert.NotNull(store.AudioPath(recording.Id));
        Assert.NotNull(store.PeaksPath(recording.Id));
        Assert.True(store.Bytes(recording.Id) > 6_400);
    }

    [Fact]
    public void StrayFilesAndDirectoriesInTheRootAreIgnored()
    {
        File.WriteAllBytes(Path.Combine(root, ".DS_Store"), []);
        Directory.CreateDirectory(Path.Combine(root, "not-a-uuid"));
        store.Write(Metadata());
        Assert.Single(store.List());
    }

    [Fact]
    public void RenameKeepsEverythingElseAndTreatsBlankAsNoTitle()
    {
        MeetingRecordingMetadata recording = Metadata(title: "Weekly sync");
        store.Write(recording);
        MeetingRecordingMetadata? renamed = store.Rename(recording.Id, "  Budget review  ");
        Assert.NotNull(renamed);
        Assert.Equal("Budget review", renamed.Title);
        Assert.Equal(12, renamed.Duration);
        Assert.Equal("Budget review", store.Metadata(recording.Id)?.Title);
        Assert.Null(store.Rename(recording.Id, "   ")?.Title);
    }

    [Fact]
    public void TrashHandsTheWholeDirectoryToTheTrasher()
    {
        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording);
        store.Trash(recording.Id);
        Assert.Equal([store.Directory(recording.Id)], trasher.Trashed);
        Assert.Empty(store.List());
    }

    /// <summary>Not upstream: without gio the store says so, and deleting for good still works.</summary>
    [Fact]
    public void WithoutATrashTheStoreSaysSoAndCanDeleteForGood()
    {
        trasher.IsAvailable = false;
        Assert.False(store.CanTrash);
        Assert.False(new GioTrasher(searchPath: "/nonexistent").IsAvailable);

        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording);
        store.DeletePermanently(recording.Id);
        Assert.Empty(store.List());
        Assert.Empty(trasher.Trashed);
    }

    [Fact]
    public void ACrashedRecordingIsRepairedAndPublishedWithItsRealDuration()
    {
        MeetingRecordingMetadata recording = Metadata();
        recording.Duration = 0;
        recording.EndReason = null;
        recording.TranscriptionState = MeetingTranscriptionState.Live;
        store.Write(recording, incomplete: true);
        WriteAudio(recording.Id, frames: 32_000, sealed_: false);

        IReadOnlyList<MeetingRecordingMetadata> recovered = store.RecoverIncomplete();
        Assert.Equal([recording.Id], recovered.Select(m => m.Id));
        Assert.Equal(2, recovered[0].Duration);
        Assert.Equal(MeetingEndReason.CrashRecovered, recovered[0].EndReason);
        Assert.Equal(MeetingTranscriptionState.Partial, recovered[0].TranscriptionState);
        Assert.Equal([recording.Id], store.List().Select(m => m.Id));
        string? audio = store.AudioPath(recording.Id);
        Assert.NotNull(audio);
        Assert.Equal(32_000u * 4, DeclaredDataBytes(audio));
        Assert.False(Directory.Exists(store.IncompleteDirectory(recording.Id)));
    }

    [Fact]
    public void ACrashedScreenRecordingPublishesItsIncompleteMovieBesideTheRepairedAudio()
    {
        MeetingRecordingMetadata recording = Metadata();
        recording.CaptureKind = RecordingCaptureKind.Screen;
        recording.VideoFileName = MeetingStore.VideoFileName;
        recording.ScreenOptions = new ScreenRecordingOptions { CameraEnabled = true };
        recording.Duration = 0;
        recording.EndReason = null;
        store.Write(recording, incomplete: true);
        WriteAudio(recording.Id, frames: 16_000, sealed_: false);
        string incompleteMovie = Path.Combine(store.IncompleteDirectory(recording.Id), MeetingStore.VideoFileName + ".incomplete");
        File.WriteAllBytes(incompleteMovie, [0, 1, 2, 3]);

        IReadOnlyList<MeetingRecordingMetadata> recovered = store.RecoverIncomplete();

        Assert.Equal([recording.Id], recovered.Select(m => m.Id));
        Assert.Equal(RecordingCaptureKind.Screen, recovered[0].CaptureKind);
        string? video = store.VideoPath(recording.Id);
        Assert.NotNull(video);
        Assert.Equal(MeetingStore.VideoFileName, Path.GetFileName(video));
        Assert.Equal([0, 1, 2, 3], File.ReadAllBytes(video));
    }

    [Fact]
    public void ACrashedRecordingWithoutMetadataStillComesBack()
    {
        var id = Guid.NewGuid();
        WriteAudio(id, frames: 16_000, sealed_: false);
        IReadOnlyList<MeetingRecordingMetadata> recovered = store.RecoverIncomplete();
        Assert.Equal([id], recovered.Select(m => m.Id));
        Assert.Equal(1, recovered[0].Duration);
        Assert.Null(recovered[0].Title);
    }

    [Fact]
    public void AnIncompleteDirectoryWithNoAudioIsSweptNotPublished()
    {
        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording, incomplete: true);
        Assert.Empty(store.RecoverIncomplete());
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.IncompleteDirectory(recording.Id)));
    }

    [Fact]
    public void ARecordingStillHeldByAnotherProcessIsLeftAlone()
    {
        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording, incomplete: true);
        string directory = store.IncompleteDirectory(recording.Id);
        WriteAudio(recording.Id, frames: 1_600, sealed_: false);
        // Still open: the lease is held, as it would be by a live sibling. SEAM(wave4a-B): upstream
        // keeps a MeetingWriter open here; the port's writer must hold audio.wav with FileShare.None.
        using (new FileStream(Path.Combine(directory, MeetingStore.AudioFileName), FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.Empty(store.RecoverIncomplete());
            Assert.True(Directory.Exists(directory));
        }
    }

    [Fact]
    public void TranscriptRoundTripsBesideTheAudio()
    {
        MeetingRecordingMetadata recording = Metadata();
        store.Write(recording);
        var transcript = new MeetingTranscript(
            utterances: [new MeetingUtterance(MeetingChannel.Microphone, 0, 2, "Hello")],
            decodedFrames: new Dictionary<MeetingChannel, int> { [MeetingChannel.Microphone] = 32_000 });
        store.Write(transcript, recording.Id);
        Assert.Equal(transcript, store.Transcript(recording.Id));
    }

    [Fact]
    public void ADamagedMetadataFileIsSkippedNotFatal()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(store.Directory(id));
        File.WriteAllText(Path.Combine(store.Directory(id), "meta.json"), "{not json");
        Assert.Null(store.Metadata(id));
        Assert.Empty(store.List());
    }
}
