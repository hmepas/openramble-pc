using System.Globalization;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Leaving the PC: a recording's transcript as Markdown and its sound as a small M4A (upstream
/// AppState.swift, "Leaving the Mac"). Where the file goes is the Save dialog's business.
/// </summary>
public sealed partial class AppState
{
    private MeetingAudioExporter audioExporter = null!;

    /// <summary>
    /// The name a file takes when it leaves, without an extension: the person's own title, or
    /// the date they would recognise it by.
    /// </summary>
    public string ExportName(Guid id)
    {
        MeetingRecordingMetadata? recording = FindRecording(id) ?? (LiveRecording is { } live && live.Id == id ? live : null);
        return MeetingExportNaming.FileName(
            recording?.Title ?? string.Empty,
            RecordingsPlaceholder.DefaultTitle(recording?.StartedAt ?? now()));
    }

    /// <summary>What the Save dialog proposes for the transcript.</summary>
    public string SuggestedTranscriptFileName(Guid id) => ExportName(id) + ".md";

    /// <summary>What the Save dialog proposes for the audio.</summary>
    public string SuggestedAudioFileName(Guid id) => ExportName(id) + ".m4a";

    /// <summary>
    /// The transcript as Markdown, or <c>null</c> when there are no words or no such finished
    /// recording. Speakers are "You" and "Others"; an imported file has none. A one-sided
    /// recording keeps the line that says so.
    /// </summary>
    public string? ExportTranscript(Guid id)
    {
        if (FindRecording(id) is not { } recording) { return null; }
        IReadOnlyList<MeetingUtterance> utterances = Transcript(id);
        if (utterances.Count == 0) { return null; }

        string subtitle = string.Join(
            " · ",
            TimeZoneInfo.ConvertTime(recording.StartedAt, TimeZoneInfo.Local)
                .ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture),
            RecordingTime.Brief(TimeSpan.FromSeconds(recording.Duration)),
            RecordingsPlaceholder.Kind(LibraryRecording.KindOf(recording)));
        return MeetingTranscriptFormatter.Markdown(
            utterances,
            recording.Title ?? RecordingsPlaceholder.DefaultTitle(recording.StartedAt),
            subtitle,
            RecordingsPlaceholder.DegradedNote(recording),
            speakers: !recording.IsImported);
    }

    /// <summary>Whether <see cref="ExportAudio"/> can work for this recording: ffmpeg and sound on disk.</summary>
    public bool CanExportAudio(Guid id) => audioExporter.IsAvailable && RecordingAudioPath(id) is not null;

    /// <summary>
    /// Encode the recording's sound (<c>audio.wav</c>, else the <c>audio.m4a</c> archive) to a mono
    /// M4A at <paramref name="destination"/>. Long on a two-hour meeting; cancel through the token.
    /// A failure says so and returns false; nothing half-written is left.
    /// </summary>
    public async Task<bool> ExportAudio(Guid id, string destination, CancellationToken cancellationToken = default)
    {
        if (RecordingAudioPath(id) is not { } source)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't save the audio: the recording has no sound on disk.", wordsDidNotLand: false));
            return false;
        }

        if (!audioExporter.IsAvailable)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't save the audio: ffmpeg is not installed.", wordsDidNotLand: false));
            return false;
        }

        bool saved = await audioExporter.Export(source, destination, cancellationToken).ConfigureAwait(false);
        if (!saved)
        {
            Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't save the audio.", wordsDidNotLand: false)
            {
                LogMessage = "recording audio export failed",
            });
        }

        return saved;
    }

    private MeetingRecordingMetadata? FindRecording(Guid id) =>
        Recordings.FirstOrDefault(recording => recording.Id == id);
}
