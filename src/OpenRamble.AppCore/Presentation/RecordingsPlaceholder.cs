using System.Globalization;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Every empty, waiting and degraded state of the Library's recordings, as data.
///
/// Held in one place so the strings can be read together and tested
/// together, the way <see cref="ModelStatus"/> does it: a placeholder decided inside a
/// view is a placeholder nobody proofreads. <c>Symbol</c> is the original's SF Symbol name;
/// the window maps it to its own icon.
/// </summary>
public sealed record RecordingsPlaceholder(string Symbol, string Title, string Detail)
{
    public static RecordingsPlaceholder EmptyLibrary { get; } = new(
        "waveform",
        "Nothing said yet",
        "Hold your dictation key to dictate, press the red button to record, or drop an audio file here. Everything stays on this computer.");

    public static RecordingsPlaceholder NothingSelected { get; } = new(
        "waveform",
        "Nothing selected",
        "Choose a dictation or a recording to read it here.");

    /// <summary>The search, or the scope, leaves nothing to show.</summary>
    public static RecordingsPlaceholder NothingFound { get; } = new(
        "magnifyingglass",
        "Nothing found",
        "No dictation or recording matches.");

    public static RecordingsPlaceholder Listening { get; } = new(
        "text.alignleft",
        "Listening",
        "Your transcript appears here as you speak.");

    public static RecordingsPlaceholder StillTranscribing { get; } = new(
        "text.alignleft",
        "Still transcribing",
        "The rest of this recording is being transcribed. The audio is complete.");

    public static RecordingsPlaceholder NoSpeech { get; } = new(
        "text.alignleft",
        "Nothing to transcribe",
        "No speech was heard in this recording.");

    public static RecordingsPlaceholder TranscriptionDidNotFinish { get; } = new(
        "text.alignleft",
        "Transcription didn't finish",
        "The audio is complete; the transcript is not.");

    public static RecordingsPlaceholder WaitingForModel { get; } = new(
        "clock",
        "Waiting for the speech model",
        "Transcription starts once the model is downloaded and ready.");

    public static RecordingsPlaceholder NotTranscribed { get; } = new(
        "text.alignleft",
        "Not transcribed",
        "This recording was interrupted before it could be transcribed.");

    /// <summary>What to show in place of an empty transcript, given how far it got.</summary>
    public static RecordingsPlaceholder Transcript(MeetingTranscriptionState state) => state switch
    {
        MeetingTranscriptionState.None => NotTranscribed,
        MeetingTranscriptionState.Live => StillTranscribing,
        MeetingTranscriptionState.Complete => NoSpeech,
        MeetingTranscriptionState.Partial or MeetingTranscriptionState.Failed => TranscriptionDidNotFinish,
        MeetingTranscriptionState.WaitingForModel => WaitingForModel,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static RecordingsPlaceholder AudioMissing { get; } = new(
        "waveform.slash",
        "Recording no longer on disk",
        "Its audio file was moved or deleted outside OpenRamble. The entry can be removed.");

    public static RecordingsPlaceholder Recovered { get; } = new(
        "waveform.badge.exclamationmark",
        "Recovered after an interruption",
        "OpenRamble stopped before this recording could end normally. Everything recorded up to that moment was kept.");

    /// <summary>
    /// A one-line explanation of how a recording ended, when it did not end
    /// by the person's hand. <c>null</c> for the ordinary case.
    /// </summary>
    public static string? EndNote(MeetingEndReason? reason) => reason switch
    {
        null or MeetingEndReason.StoppedByUser => null,
        MeetingEndReason.DiskFull => "Stopped because this computer ran out of space. Everything up to that moment was kept.",
        MeetingEndReason.WriteFailed => "Stopped because the recording could no longer be written. Everything up to that moment was kept.",
        MeetingEndReason.ApplicationQuit => "Stopped when OpenRamble quit.",
        MeetingEndReason.CrashRecovered => "Recovered after an interruption. Everything recorded up to that moment was kept.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// The line a recording carries forever when a requested side never arrived.
    /// <paramref name="microphoneEverDeliveredAudio"/> is <c>null</c> for files older than the field.
    /// </summary>
    public static string? DegradedNote(bool? microphoneEverDeliveredAudio, bool systemAudioWasRequested, bool systemAudioEverDeliveredAudio)
    {
        bool microphoneMissing = microphoneEverDeliveredAudio == false;
        bool othersMissing = systemAudioWasRequested && !systemAudioEverDeliveredAudio;
        return (microphoneMissing, othersMissing) switch
        {
            (true, true) => "Neither your microphone nor the other side of this call was captured.",
            (true, false) => systemAudioWasRequested
                ? "The other side of this call was recorded. Your microphone was not captured."
                : "Your microphone was not captured.",
            (false, true) => "Only your microphone was recorded. The other side of this call was not captured.",
            (false, false) => null,
        };
    }

    /// <summary>What kind of thing a recording is, in two words.</summary>
    public static string Kind(RecordingKind kind) => kind switch
    {
        RecordingKind.ScreenRecording => "Screen recording",
        RecordingKind.ImportedFile => "Imported file",
        RecordingKind.Meeting => "Meeting",
        RecordingKind.VoiceNote => "Voice note",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static RecordingsPlaceholder ImportTranscribing { get; } = new(
        "text.alignleft",
        "Transcribing",
        "The words appear here as they are read.");

    public static RecordingsPlaceholder ImportWaiting { get; } = new(
        "clock",
        "Waiting its turn",
        "Another file is being transcribed first. You can already play this one.");

    /// <summary>
    /// What stands in for the words of an imported file that has none yet,
    /// or <c>null</c> when the importer is not working on it.
    /// </summary>
    public static RecordingsPlaceholder? Importing(Guid id, RecordingImportActivity activity)
    {
        if (activity.Transcribing == id) { return ImportTranscribing; }
        if (!activity.Waiting.Contains(id)) { return null; }
        return activity.NeedsModel && activity.Transcribing is null ? WaitingForModel : ImportWaiting;
    }

    /// <summary>
    /// What an imported recording says while its words are still coming, or
    /// <c>null</c> once they are all there.
    /// </summary>
    public static string? ImportLine(Guid id, RecordingImportActivity activity, bool isDictating)
    {
        if (activity.Transcribing == id)
        {
            if (isDictating) { return "Transcription paused while you dictate."; }
            return $"Transcribing — {(int)Math.Floor(activity.Fraction * 100)} percent.";
        }
        if (!activity.Waiting.Contains(id)) { return null; }
        // Only the first in line waits for a model; the rest wait for it.
        if (activity.NeedsModel && activity.Transcribing is null) { return "Waiting for the speech model."; }
        return "Waiting its turn.";
    }

    /// <summary>The title a recording shows when the person has not given it one.</summary>
    public static string DefaultTitle(DateTimeOffset startedAt, TimeZoneInfo? timeZone = null) =>
        TimeZoneInfo.ConvertTime(startedAt, timeZone ?? TimeZoneInfo.Local)
            .ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// What the file importer is doing, as the Library shows it (upstream
/// <c>RecordingImporter.Activity</c>; the importer publishes this type).
/// </summary>
public sealed record RecordingImportActivity
{
    /// <summary>Files being read into the Library. They have no row yet.</summary>
    public int Copying { get; init; }

    /// <summary>The recording being transcribed.</summary>
    public Guid? Transcribing { get; init; }

    /// <summary>How far <see cref="Transcribing"/> has got, 0...1.</summary>
    public double Fraction { get; init; }

    /// <summary>Imported recordings whose turn has not come, in order.</summary>
    public IReadOnlyList<Guid> Waiting { get; init; } = [];

    /// <summary>The first waiting recording needs a speech model that is not ready.</summary>
    public bool NeedsModel { get; init; }

    public bool IsIdle => Copying == 0 && Transcribing is null && Waiting.Count == 0;
}
