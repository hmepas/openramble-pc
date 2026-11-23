using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>Which paragraph of a transcript is being heard (upstream TranscriptView.isCurrent).</summary>
public static class TranscriptPlayback
{
    /// <summary>
    /// The paragraph with start ≤ <paramref name="time"/> &lt; end, or <c>null</c> - between
    /// paragraphs, or when nothing has been played (<paramref name="time"/> is <c>null</c>).
    /// </summary>
    public static Guid? Current(IEnumerable<MeetingUtterance> utterances, double? time)
    {
        if (time is not { } at) { return null; }
        return utterances.FirstOrDefault(utterance => utterance.Start <= at && at < utterance.End)?.Id;
    }
}

/// <summary>
/// The transport slider while a person drags it (upstream RecordingTransport.swift): dragging
/// pauses and the thumb follows the pointer, not the player; letting go seeks there and plays on
/// if it was playing before.
/// </summary>
public sealed class TransportScrub
{
    private enum Phase
    {
        Idle,
        Scrubbing,
    }

    private Phase phase = Phase.Idle;
    private bool wasPlaying;
    private double target;

    public bool IsScrubbing => phase == Phase.Scrubbing;

    /// <summary>A drag starts. Returns whether the player should pause for it.</summary>
    public bool Begin(bool isPlaying, double at)
    {
        if (phase == Phase.Scrubbing) { return false; }
        phase = Phase.Scrubbing;
        wasPlaying = isPlaying;
        target = at;
        return isPlaying;
    }

    /// <summary>The thumb moved under the pointer; ignored outside a drag.</summary>
    public void Move(double to)
    {
        if (phase == Phase.Scrubbing) { target = to; }
    }

    /// <summary>The drag ended: where to seek, and whether to play on. <c>null</c> outside a drag.</summary>
    public (double SeekTo, bool Resume)? End()
    {
        if (phase != Phase.Scrubbing) { return null; }
        phase = Phase.Idle;
        return (target, wasPlaying);
    }

    /// <summary>Another recording, or the view went away: the drag is forgotten, nothing resumes.</summary>
    public void Cancel() => phase = Phase.Idle;

    /// <summary>What the slider and the clock show: the drag while dragging, else the player.</summary>
    public double Displayed(double playerPosition) => phase == Phase.Scrubbing ? target : playerPosition;
}
