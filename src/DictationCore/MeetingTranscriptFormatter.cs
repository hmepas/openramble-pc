using System.Globalization;

namespace DictationCore;

/// <summary>
/// A transcript as text - for the clipboard, a file, or another app (upstream
/// <c>MeetingTranscriptFormatter.swift</c>).
/// </summary>
/// <remarks>
/// Speaker names come from the channel at render time, so relabelling is a metadata edit rather
/// than a rewrite. Timestamps are always included: a checkbox for them is exactly the kind of
/// option this app refuses, and removing them is a find-and-replace in any editor. Pure.
/// </remarks>
public static class MeetingTranscriptFormatter
{
    public const string FailedLine = "[couldn't transcribe this part]";

    /// <summary>Upstream says "produced on this Mac"; the port says PC, as elsewhere.</summary>
    public const string ProvenanceLine = "> Recorded with OpenRamble. This transcript was produced on this PC.";

    public static IReadOnlyDictionary<MeetingChannel, string> DefaultNames { get; } =
        new Dictionary<MeetingChannel, string> { [MeetingChannel.Microphone] = "You", [MeetingChannel.System] = "Others" };

    /// <param name="speakers">
    /// <c>false</c> for an imported file: its one stream of sound is on the microphone's channel,
    /// and it is not "You" - who speaks in a file is not something the app knows.
    /// </param>
    public static string PlainText(
        IEnumerable<MeetingUtterance> utterances,
        IReadOnlyDictionary<MeetingChannel, string>? names = null,
        bool speakers = true) =>
        string.Join(
            "\n\n",
            Ordered(utterances).Select(utterance =>
            {
                string heading = speakers
                    ? $"{Name(utterance.Channel, names)} · {Timestamp(utterance.Start)}"
                    : Timestamp(utterance.Start);
                return $"{heading}\n{Body(utterance)}";
            }));

    /// <param name="note">
    /// A line that survives in the artefact - "only the microphone was recorded" - when the
    /// recording was one-sided.
    /// </param>
    /// <param name="speakers">As for <see cref="PlainText"/>.</param>
    public static string Markdown(
        IEnumerable<MeetingUtterance> utterances,
        string title,
        string subtitle,
        string? note = null,
        IReadOnlyDictionary<MeetingChannel, string>? names = null,
        bool speakers = true)
    {
        List<string> lines = [$"# {title}", string.Empty, subtitle, string.Empty, ProvenanceLine];
        if (note is not null) { lines.AddRange([string.Empty, $"> {note}"]); }
        foreach (MeetingUtterance utterance in Ordered(utterances))
        {
            string heading = speakers
                ? $"**{Name(utterance.Channel, names)}** · {Timestamp(utterance.Start)}"
                : $"**{Timestamp(utterance.Start)}**";
            lines.AddRange([string.Empty, heading, Body(utterance)]);
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary><c>hh:mm:ss</c>, always three fields, so a column of them lines up.</summary>
    public static string Timestamp(double seconds)
    {
        int total = Math.Max(0, (int)Math.Floor(seconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{total / 3_600:00}:{total % 3_600 / 60:00}:{total % 60:00}");
    }

    public static string Name(MeetingChannel channel, IReadOnlyDictionary<MeetingChannel, string>? names = null) =>
        names is not null && names.TryGetValue(channel, out string? name) ? name : DefaultNames[channel];

    private static IOrderedEnumerable<MeetingUtterance> Ordered(IEnumerable<MeetingUtterance> utterances) =>
        utterances.OrderBy(utterance => utterance.Start);

    private static string Body(MeetingUtterance utterance) => utterance.IsFailed ? FailedLine : utterance.Text;
}
