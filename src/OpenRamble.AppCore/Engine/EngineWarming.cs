namespace OpenRamble.AppCore;

/// <summary>
/// When a recording starts, is the engine cold enough to warm in parallel?
///
/// The recognition engine has two speeds: warm inference is a fraction of a second, a reload
/// after the system reclaimed the session's memory is seconds. Eviction happens with idle time -
/// memory pressure, sleep - so the moment the person presses the key is the perfect place to pay
/// for the reload: they are about to speak for seconds anyway, and the warm-up runs under their
/// voice instead of after it.
///
/// The five-minute threshold keeps the ping out of active work entirely: back-to-back dictations
/// never trigger it, only a return from real idle.
/// </summary>
public static class EngineWarming
{
    public static TimeSpan ColdSuspicionInterval { get; } = TimeSpan.FromSeconds(300);

    public static bool ShouldWarm(DateTimeOffset? lastEngineActivity, DateTimeOffset now)
    {
        if (lastEngineActivity is not DateTimeOffset last) { return true; }
        return now - last >= ColdSuspicionInterval;
    }
}
