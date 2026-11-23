using System.Globalization;

namespace DictationCore;

/// <summary>
/// A transcript as text, for the clipboard (upstream <c>MeetingTranscriptFormatter.swift</c>;
/// only the plain-text half is ported so far).
/// </summary>
/// <remarks>
/// Speaker names come from the channel at render time. Timestamps are always included. Pure.
/// </remarks>
public static class MeetingTranscriptFormatter
{
    public const string FailedLine = "[couldn't transcribe this part]";

    /// <param name="speakers">
    /// <c>false</c> for an imported file: its one stream of sound is on the microphone's channel,
    /// and it is not "You" - who speaks in a file is not something the app knows.
    /// </param>
    public static string PlainText(IEnumerable<MeetingUtterance> utterances, bool speakers = true) =>
        string.Join(
            "\n\n",
            utterances.OrderBy(utterance => utterance.Start).Select(utterance =>
            {
                string heading = speakers
                    ? $"{Name(utterance.Channel)} · {Timestamp(utterance.Start)}"
                    : Timestamp(utterance.Start);
                return $"{heading}\n{(utterance.IsFailed ? FailedLine : utterance.Text)}";
            }));

    /// <summary><c>hh:mm:ss</c>, always three fields, so a column of them lines up.</summary>
    public static string Timestamp(double seconds)
    {
        int total = Math.Max(0, (int)Math.Floor(seconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{total / 3_600:00}:{total % 3_600 / 60:00}:{total % 60:00}");
    }

    private static string Name(MeetingChannel channel) =>
        channel == MeetingChannel.System ? "Others" : "You";
}
