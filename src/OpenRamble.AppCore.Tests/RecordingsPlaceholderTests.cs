using DictationCore;
using Xunit;

namespace OpenRamble.AppCore.Tests;

public class RecordingsPlaceholderTests
{
    [Fact]
    public void AnOrdinaryEndingNeedsNoNote()
    {
        Assert.Null(RecordingsPlaceholder.EndNote(null));
        Assert.Null(RecordingsPlaceholder.EndNote(MeetingEndReason.StoppedByUser));
    }

    [Fact]
    public void EveryOtherEndingSaysWhatWasKept()
    {
        foreach (var reason in new[] { MeetingEndReason.DiskFull, MeetingEndReason.WriteFailed, MeetingEndReason.CrashRecovered })
        {
            string? note = RecordingsPlaceholder.EndNote(reason);
            Assert.NotNull(note);
            Assert.Contains("kept", note);
        }
        Assert.NotNull(RecordingsPlaceholder.EndNote(MeetingEndReason.ApplicationQuit));
    }

    [Fact]
    public void ListeningSaysTheTextWaitsForAPause()
    {
        Assert.Equal("Listening", RecordingsPlaceholder.Listening.Title);
        Assert.Contains("transcript", RecordingsPlaceholder.Listening.Detail.ToLowerInvariant());
    }

    [Fact]
    public void PlaceholdersNeverUseRedLanguageOrBlame()
    {
        foreach (var placeholder in new[]
        {
            RecordingsPlaceholder.EmptyLibrary, RecordingsPlaceholder.NothingSelected, RecordingsPlaceholder.NotTranscribed,
            RecordingsPlaceholder.AudioMissing, RecordingsPlaceholder.Recovered, RecordingsPlaceholder.Listening,
            RecordingsPlaceholder.StillTranscribing, RecordingsPlaceholder.NoSpeech,
            RecordingsPlaceholder.TranscriptionDidNotFinish, RecordingsPlaceholder.WaitingForModel,
        })
        {
            Assert.NotEmpty(placeholder.Title);
            Assert.NotEmpty(placeholder.Detail);
            Assert.DoesNotContain("error", placeholder.Detail.ToLowerInvariant());
        }
    }

    [Fact]
    public void TheDefaultTitleIsTheDate()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_756_900_000);
        Assert.Equal("3 Sep 2025, 11:46", RecordingsPlaceholder.DefaultTitle(date, TimeZoneInfo.Utc));
    }

    [Fact]
    public void EveryTranscriptStateHasAPlaceholder()
    {
        Assert.Equal(RecordingsPlaceholder.StillTranscribing, RecordingsPlaceholder.Transcript(MeetingTranscriptionState.Live));
        Assert.Equal(RecordingsPlaceholder.NoSpeech, RecordingsPlaceholder.Transcript(MeetingTranscriptionState.Complete));
        Assert.Equal(RecordingsPlaceholder.TranscriptionDidNotFinish, RecordingsPlaceholder.Transcript(MeetingTranscriptionState.Partial));
        Assert.Equal(RecordingsPlaceholder.WaitingForModel, RecordingsPlaceholder.Transcript(MeetingTranscriptionState.WaitingForModel));
        Assert.Equal(RecordingsPlaceholder.NotTranscribed, RecordingsPlaceholder.Transcript(MeetingTranscriptionState.None));
    }

    [Fact]
    public void AOneSidedMeetingCarriesItsNoteAndAVoiceNoteDoesNot()
    {
        Assert.NotNull(RecordingsPlaceholder.DegradedNote(null, systemAudioWasRequested: true, systemAudioEverDeliveredAudio: false));
        Assert.Null(RecordingsPlaceholder.DegradedNote(null, systemAudioWasRequested: true, systemAudioEverDeliveredAudio: true));
        Assert.Null(RecordingsPlaceholder.DegradedNote(null, systemAudioWasRequested: false, systemAudioEverDeliveredAudio: false));
    }

    [Fact]
    public void AMeetingThatMissedTheMicrophoneCarriesTheInverseNote()
    {
        Assert.Equal(
            "The other side of this call was recorded. Your microphone was not captured.",
            RecordingsPlaceholder.DegradedNote(false, systemAudioWasRequested: true, systemAudioEverDeliveredAudio: true));
        Assert.Equal(
            "Neither your microphone nor the other side of this call was captured.",
            RecordingsPlaceholder.DegradedNote(false, systemAudioWasRequested: true, systemAudioEverDeliveredAudio: false));
        Assert.Equal(
            "Your microphone was not captured.",
            RecordingsPlaceholder.DegradedNote(false, systemAudioWasRequested: false, systemAudioEverDeliveredAudio: false));
        // Old files must not look degraded.
        Assert.Null(RecordingsPlaceholder.DegradedNote(null, systemAudioWasRequested: true, systemAudioEverDeliveredAudio: true));
    }

    [Fact]
    public void AnImportSaysWhereItIsInLine()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var waitingForModel = new RecordingImportActivity { Waiting = [first, second], NeedsModel = true };
        Assert.Equal("Waiting for the speech model.", RecordingsPlaceholder.ImportLine(first, waitingForModel, isDictating: false));
        Assert.Equal(RecordingsPlaceholder.WaitingForModel, RecordingsPlaceholder.Importing(first, waitingForModel));

        var running = new RecordingImportActivity { Transcribing = first, Fraction = 0.379, Waiting = [second] };
        Assert.Equal("Transcribing — 37 percent.", RecordingsPlaceholder.ImportLine(first, running, isDictating: false));
        Assert.Equal("Transcription paused while you dictate.", RecordingsPlaceholder.ImportLine(first, running, isDictating: true));
        Assert.Equal("Waiting its turn.", RecordingsPlaceholder.ImportLine(second, running, isDictating: false));
        Assert.Equal(RecordingsPlaceholder.ImportWaiting, RecordingsPlaceholder.Importing(second, running));
        Assert.Null(RecordingsPlaceholder.ImportLine(Guid.NewGuid(), running, isDictating: false));
        Assert.Null(RecordingsPlaceholder.Importing(Guid.NewGuid(), running));
    }
}
