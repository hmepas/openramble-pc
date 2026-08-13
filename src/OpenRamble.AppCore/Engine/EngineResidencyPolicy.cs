namespace OpenRamble.AppCore;

/// <summary>The system's memory-pressure tier, as the policy sees it.</summary>
public enum MemoryPressureTier
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// When to give the engine's memory back to the system — automatically.
///
/// Other dictation apps ask the user to pick an unload timer from seven options. This is the
/// zero-settings answer: the OS itself says when memory is tight, and the policy reacts -
/// evaluated only on events (a pressure notification, a finished dictation, a finished load,
/// wake), never by polling. Reloading is cheap to the person because it rides under their voice
/// at the next keypress.
///
/// The rules:
/// <list type="bullet">
/// <item><description>critical pressure and a safely idle engine → unload now; critical overrides
/// the hysteresis (release as much as possible).</description></item>
/// <item><description>warning pressure must persist (≥2 consecutive events) AND the engine must
/// have been idle ≥5 minutes → unload. One warning blip does nothing.</description></item>
/// <item><description>hysteresis, warning tier only: never unload within 10 minutes of the last
/// completed load — an ambient-pressure machine must not oscillate between multi-second loads
/// and unloads.</description></item>
/// <item><description>there is NO unconditional time-based unload: a healthy machine keeps the
/// engine warm forever, because manufacturing cold starts for a benefit nobody can feel would
/// attack the product's own identity.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The tiers arrive differently here. macOS hands a three-level
/// <c>DispatchSource.makeMemoryPressureSource</c> event; Windows has
/// <c>CreateMemoryResourceNotification</c>, which is a single low-memory edge, so the app derives
/// the tier from the system-wide commit load. The mapping is the caller's business - this file
/// is unchanged policy and stays testable without a memory-starved machine.
/// </remarks>
public static class EngineResidencyPolicy
{
    public static TimeSpan WarningIdleThreshold { get; } = TimeSpan.FromSeconds(300);

    public const int WarningEventsRequired = 2;

    public static TimeSpan PostLoadHold { get; } = TimeSpan.FromSeconds(600);

    public abstract record Decision
    {
        private Decision()
        {
        }

        public sealed record Keep : Decision;

        public sealed record Unload : Decision;

        /// <summary>
        /// A rule is pending a time boundary: re-evaluate after this delay. The caller arms ONE
        /// one-shot timer; at rest there is no timer.
        /// </summary>
        public sealed record CheckAgain(TimeSpan After) : Decision;
    }

    public static Decision Decide(
        MemoryPressureTier tier,
        int consecutiveWarnings,
        bool engineLoaded,
        bool engineBusy,
        TimeSpan idleFor,
        TimeSpan sinceLoadCompleted)
    {
        if (!engineLoaded) { return new Decision.Keep(); }

        // Never interleave with work; completion events re-evaluate.
        if (engineBusy) { return new Decision.Keep(); }

        switch (tier)
        {
            case MemoryPressureTier.Critical:
                return new Decision.Unload();

            case MemoryPressureTier.Warning:
                if (consecutiveWarnings < WarningEventsRequired) { return new Decision.Keep(); }

                bool idleReady = idleFor >= WarningIdleThreshold;
                bool holdExpired = sinceLoadCompleted >= PostLoadHold;
                if (idleReady && holdExpired)
                {
                    return new Decision.Unload();
                }

                // Waiting on a boundary: come back exactly when the later of the two windows
                // opens.
                TimeSpan untilIdle = Remaining(WarningIdleThreshold, idleFor);
                TimeSpan untilHold = Remaining(PostLoadHold, sinceLoadCompleted);
                return new Decision.CheckAgain(Max(untilIdle, untilHold));

            case MemoryPressureTier.Normal:
                return new Decision.Keep();

            default:
                throw new ArgumentOutOfRangeException(nameof(tier));
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    /// <summary>
    /// How long until a window opens. Swift subtracts and clamps; here the comparison comes
    /// first, because <c>elapsed</c> is <see cref="TimeSpan.MaxValue"/> when the event never
    /// happened ("infinitely idle") and subtracting from that overflows rather than clamping.
    /// </summary>
    private static TimeSpan Remaining(TimeSpan window, TimeSpan elapsed) =>
        elapsed >= window ? TimeSpan.Zero : window - elapsed;
}
