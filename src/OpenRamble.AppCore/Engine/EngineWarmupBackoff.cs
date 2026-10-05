namespace OpenRamble.AppCore;

/// <summary>
/// How long to wait before trying the model preparation again.
///
/// Preparation has no alternative: without a warm recognizer there is no product, and the files
/// on disk are already verified. So an attempt that fails is not a verdict the person has to act
/// on - it is one more attempt pending. The app keeps trying for as long as it is open, backing
/// off so a busy machine is not hammered, and the interface simply keeps saying "preparing"
/// instead of manufacturing an error state with a button.
///
/// The ladder matters more than any single value: the first attempts are for a transient hiccup
/// (a machine still flushing the hundreds of megabytes it just wrote), the tail is for a machine
/// that is genuinely busy right now and will be free later.
/// </summary>
public static class EngineWarmupBackoff
{
    public static IReadOnlyList<TimeSpan> Ladder { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary><paramref name="attempt"/> counts failures so far, starting at zero.</summary>
    public static TimeSpan Delay(int attempt)
    {
        if (attempt <= 0) { return Ladder[0]; }
        return Ladder[Math.Min(attempt, Ladder.Count - 1)];
    }
}
