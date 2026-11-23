using DictationCore;

namespace OpenRamble.AppCore.Tests;

public sealed class LibraryRecordingTests
{
    private static MeetingRecordingMetadata Recording(
        bool systemAudioRequested = false,
        RecordingCaptureKind captureKind = RecordingCaptureKind.Audio,
        RecordingImportLanguage? importLanguage = null) =>
        MeetingRecordingMetadata.Create(
            startedAt: DateTimeOffset.FromUnixTimeSeconds(1_000),
            systemAudio: new SystemAudioSummary(wasRequested: systemAudioRequested),
            duration: 90,
            title: "Standup",
            captureKind: captureKind,
            importLanguage: importLanguage);

    [Fact]
    public void KindIsScreenThenImportedThenMeetingThenVoiceNote()
    {
        Assert.Equal(RecordingKind.VoiceNote, new LibraryRecording(Recording()).Kind);
        Assert.Equal(RecordingKind.Meeting, new LibraryRecording(Recording(systemAudioRequested: true)).Kind);
        Assert.Equal(
            RecordingKind.ImportedFile,
            new LibraryRecording(Recording(systemAudioRequested: true, importLanguage: RecordingImportLanguage.Automatic)).Kind);
        Assert.Equal(
            RecordingKind.ScreenRecording,
            new LibraryRecording(Recording(
                systemAudioRequested: true,
                captureKind: RecordingCaptureKind.Screen,
                importLanguage: RecordingImportLanguage.Hebrew)).Kind);
    }

    [Fact]
    public void TheMetadataAndTranscriptShowThrough()
    {
        MeetingRecordingMetadata metadata = Recording();
        var recording = new LibraryRecording(metadata, "Hello there");
        Assert.Equal(metadata.Id, recording.Id);
        Assert.Equal(metadata.StartedAt, recording.StartedAt);
        Assert.Equal("Standup", recording.Title);
        Assert.Equal(TimeSpan.FromSeconds(90), recording.Duration);
        Assert.Equal("Hello there", recording.TranscriptText);
    }

    [Fact]
    public void TheDegradedNoteReadsTheMetadata()
    {
        MeetingRecordingMetadata metadata = Recording(systemAudioRequested: true);
        Assert.Equal(
            "Only your microphone was recorded. The other side of this call was not captured.",
            RecordingsPlaceholder.DegradedNote(metadata));
        metadata.SystemAudio.EverDeliveredAudio = true;
        Assert.Null(RecordingsPlaceholder.DegradedNote(metadata));
    }
}
