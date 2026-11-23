namespace OpenRamble.AppCore;

/// <summary>
/// Durations and positions as the window shows them and as a screen reader says
/// them.
///
/// The two forms are kept side by side because they must agree: a scrubber
/// whose label reads "48:12" and whose spoken value says something else has taught
/// a blind person to distrust the label.
/// </summary>
public static class RecordingTime
{
    /// <summary><c>m:ss</c> under an hour, <c>h:mm:ss</c> from then on. Negative reads as zero.</summary>
    public static string Clock(TimeSpan time)
    {
        int total = Math.Max(0, (int)Math.Floor(time.TotalSeconds));
        int hours = total / 3_600;
        int minutes = total % 3_600 / 60;
        int secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }

    /// <summary>The same value in words: "48 minutes 12 seconds".</summary>
    public static string Spoken(TimeSpan time)
    {
        int total = Math.Max(0, (int)Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero));
        int hours = total / 3_600;
        int minutes = total % 3_600 / 60;
        int secs = total % 60;
        var parts = new List<string>();
        if (hours > 0) { parts.Add(SpokenUnit(hours, "hour")); }
        if (minutes > 0) { parts.Add(SpokenUnit(minutes, "minute")); }
        if (secs > 0 || parts.Count == 0) { parts.Add(SpokenUnit(secs, "second")); }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// For a header: whole minutes once past one, seconds before that.
    /// Floored like <see cref="Clock"/>, so the two never disagree about the same file.
    /// </summary>
    public static string Brief(TimeSpan time)
    {
        int total = Math.Max(0, (int)Math.Floor(time.TotalSeconds));
        if (total < 60) { return $"{total} s"; }
        int hours = total / 3_600;
        int minutes = total % 3_600 / 60;
        if (hours > 0) { return minutes > 0 ? $"{hours} h {minutes} min" : $"{hours} h"; }
        return $"{minutes} min";
    }

    private static string SpokenUnit(int value, string name) => value == 1 ? $"{value} {name}" : $"{value} {name}s";
}
