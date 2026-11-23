namespace DictationCore;

// TEMPORARY (wave 4a task C): the transcript types MeetingUtteranceAssembler and the Library
// presentation need, ported from upstream MeetingRecording.swift ahead of task A. When task A's
// MeetingRecording.cs lands, delete this file; A's definitions must keep these names and shapes.

/// <summary>Which side of a recording a stretch of audio came from.</summary>
public enum MeetingChannel
{
    Microphone,
    System,
}

public enum MeetingEndReason
{
    StoppedByUser,
    DiskFull,
    WriteFailed,
    ApplicationQuit,
    /// <summary>The app died mid-recording and the file was repaired on the next launch.</summary>
    CrashRecovered,
}

public enum MeetingTranscriptionState
{
    /// <summary>Never attempted.</summary>
    None,
    Live,
    Complete,
    Partial,
    Failed,
    WaitingForModel,
}

/// <summary>One paragraph of a recording's transcript. <c>Start</c>/<c>End</c> are on the recording's timeline.</summary>
/// <param name="IsFailed">
/// The decode of this stretch failed. The audio is intact; it can be tried again, and until then
/// the transcript says so rather than skipping it.
/// </param>
public sealed record MeetingUtterance(
    Guid Id,
    MeetingChannel Channel,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    bool IsFailed = false)
{
    public MeetingUtterance(MeetingChannel channel, TimeSpan start, TimeSpan end, string text, bool isFailed = false)
        : this(Guid.NewGuid(), channel, start, end, text, isFailed)
    {
    }
}
