namespace DictationCore;

/// <summary>
/// When a recording may start, and when it must stop, for want of disk.
/// </summary>
/// <remarks>
/// Stereo 16 kHz 16-bit is 64 kB/s: 230 MB an hour. The floor to start is enough for two hours;
/// the floor to stop leaves the rest of the machine room to keep working, and a recording that
/// stops cleanly at 41:12 with a sentence saying so is worth more than one that dies at 41:13 with
/// a truncated file.
/// </remarks>
public static class MeetingDiskPolicy
{
    public const int BytesPerSecond = 64_000;
    public const long MinimumFreeBytesToStart = 500L * 1_024 * 1_024;
    public const long WarnBelowFreeBytes = 2L * 1_024 * 1_024 * 1_024;
    public const long StopBelowFreeBytes = 200L * 1_024 * 1_024;

    /// <summary>How often, in recorded frames, to look at the disk while recording.</summary>
    public const int CheckEveryFrames = 60 * 16_000;

    /// <summary>Whether a recording may start (the Swift original is an enum with associated values).</summary>
    public abstract record Verdict
    {
        private Verdict()
        {
        }

        public sealed record Ok : Verdict;

        public sealed record Low(int MinutesLeft) : Verdict;

        public sealed record TooLowToStart : Verdict;
    }

    public static Verdict VerdictToStart(long freeBytes)
    {
        if (freeBytes < MinimumFreeBytesToStart) { return new Verdict.TooLowToStart(); }
        if (freeBytes < WarnBelowFreeBytes)
        {
            return new Verdict.Low((int)(freeBytes / BytesPerSecond / 60));
        }

        return new Verdict.Ok();
    }

    public static bool MustStop(long freeBytes) => freeBytes < StopBelowFreeBytes;
}
