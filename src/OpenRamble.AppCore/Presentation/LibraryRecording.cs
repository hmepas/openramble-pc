using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>A stored recording, as the Library lists and searches it.</summary>
public sealed class LibraryRecording(MeetingRecordingMetadata metadata, string transcriptText = "") : ILibraryRecording
{
    public MeetingRecordingMetadata Metadata { get; } = metadata;

    public Guid Id => Metadata.Id;

    public DateTimeOffset StartedAt => Metadata.StartedAt;

    public string? Title => Metadata.Title;

    public TimeSpan Duration => TimeSpan.FromSeconds(Metadata.Duration);

    public RecordingKind Kind => KindOf(Metadata);

    public string TranscriptText { get; } = transcriptText;

    /// <summary>Screen first, then imported, then whether the other side was listened to.</summary>
    public static RecordingKind KindOf(MeetingRecordingMetadata recording)
    {
        if (recording.CaptureKind == RecordingCaptureKind.Screen) { return RecordingKind.ScreenRecording; }
        if (recording.IsImported) { return RecordingKind.ImportedFile; }
        return recording.IsMeeting ? RecordingKind.Meeting : RecordingKind.VoiceNote;
    }
}
