namespace DictationCore;

/// <summary>
/// The two sides of a conversation, as the two channels of one file.
///
/// A meeting is recorded from two physically separate streams - the microphone hears the
/// person at the computer, the system output carries everyone else - and that separation is
/// the whole speaker story. Nothing has to guess who spoke: the channel is the label. A voice
/// note is a meeting with one participant, and it uses the same file with the second channel
/// silent, so the format never changes underneath a recording.
/// </summary>
/// <remarks>Upstream declares this in <c>MeetingRecording.swift</c>; it lives in its own file
/// here so the audio-on-disk types do not depend on the recording model.</remarks>
public enum MeetingChannel
{
    Microphone,
    System,
}

/// <summary>
/// A stretch of one channel, by position in the file - never a copy of the audio. The frames
/// are already on disk, and whoever decodes this reads them back when its turn comes.
/// </summary>
public readonly record struct MeetingSegmentRef(MeetingChannel Channel, int StartFrame, int FrameCount)
{
    public int EndFrame => StartFrame + FrameCount;
}
