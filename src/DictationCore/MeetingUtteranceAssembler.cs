using System.Text;

namespace DictationCore;

/// <summary>
/// Decoded segments in, readable paragraphs out.
///
/// A segment is where the audio happened to be cut; a paragraph is what a
/// person reads. Same-channel segments merge until something closes the
/// paragraph: the other side speaking (exact, because the channel is the
/// speaker), a pause longer than <c>Gap</c>, a sentence ending once the paragraph
/// is long enough to read, or a hard length past which any paragraph is a
/// wall. The thresholds are the ones a sibling project measured against real
/// meeting transcripts.
///
/// Gaps are computed from the segments' frame positions, so none of this
/// needs an engine timestamp. Pure.
/// </summary>
public sealed class MeetingUtteranceAssembler
{
    public sealed record Parameters
    {
        /// <summary>A same-channel silence longer than this starts a new paragraph.</summary>
        public TimeSpan Gap { get; init; } = TimeSpan.FromSeconds(1.2);

        /// <summary>Past this, a sentence ending closes the paragraph.</summary>
        public TimeSpan SoftDuration { get; init; } = TimeSpan.FromSeconds(12);

        /// <summary>Past this, anything closes it.</summary>
        public TimeSpan HardDuration { get; init; } = TimeSpan.FromSeconds(40);
    }

    /// <summary>One decoded segment on the recording's timeline.</summary>
    public readonly record struct Segment(MeetingChannel Channel, TimeSpan Start, TimeSpan End, string Text, bool IsFailed = false);

    private MeetingUtterance? open;

    public MeetingUtteranceAssembler(Parameters? parameters = null)
    {
        Params = parameters ?? new Parameters();
    }

    public Parameters Params { get; }

    /// <summary>Feed one segment; get back whatever paragraphs it closed.</summary>
    public IReadOnlyList<MeetingUtterance> Append(Segment segment)
    {
        string text = segment.Text.Trim();
        // Silence decodes to nothing. It is not a paragraph and it does not
        // close one - a breath between two sentences is not a topic change.
        if (!segment.IsFailed && text.Length == 0) { return []; }

        var closed = new List<MeetingUtterance>();
        if (open is { } current && ShouldClose(current, segment))
        {
            closed.Add(current);
            open = null;
        }
        if (open is { } merging && !segment.IsFailed)
        {
            open = merging with
            {
                End = merging.End > segment.End ? merging.End : segment.End,
                Text = merging.Text.Length == 0 ? text : merging.Text + " " + text,
            };
        }
        else
        {
            open = new MeetingUtterance(segment.Channel, segment.Start, segment.End, segment.IsFailed ? "" : text, segment.IsFailed);
            // A failure is its own paragraph, closed at once: nothing merges
            // into a hole in the transcript.
            if (segment.IsFailed)
            {
                closed.Add(open);
                open = null;
            }
        }
        return closed;
    }

    /// <summary>The paragraph in progress - shown live, closed by whatever comes next.</summary>
    public MeetingUtterance? Pending => open;

    /// <summary>The paragraph still open, at the end.</summary>
    public IReadOnlyList<MeetingUtterance> Flush()
    {
        MeetingUtterance? last = open;
        open = null;
        return last is null ? [] : [last];
    }

    private bool ShouldClose(MeetingUtterance current, Segment segment)
    {
        if (current.IsFailed || segment.IsFailed) { return true; }
        if (segment.Channel != current.Channel) { return true; }
        if (segment.Start - current.End > Params.Gap) { return true; }
        TimeSpan duration = current.End - current.Start;
        if (duration >= Params.HardDuration) { return true; }
        if (duration >= Params.SoftDuration && EndsSentence(current.Text)) { return true; }
        return false;
    }

    /// <summary>
    /// Ends in <c>.</c>, <c>!</c>, <c>?</c> or <c>…</c>, allowing a closing quote or bracket after.
    /// Counts Unicode scalars (runes), like the original.
    /// </summary>
    internal static bool EndsSentence(string text)
    {
        const string closers = "\"'»)]”’";
        const string terminals = ".!?…";
        Rune[] runes = text.EnumerateRunes().ToArray();
        int index = runes.Length - 1;
        while (index >= 0 && closers.Contains(runes[index].ToString(), StringComparison.Ordinal)) { index--; }
        if (index < 0) { return false; }
        return terminals.Contains(runes[index].ToString(), StringComparison.Ordinal);
    }
}
