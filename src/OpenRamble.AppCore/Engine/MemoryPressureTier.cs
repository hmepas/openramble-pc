namespace OpenRamble.AppCore;

/// <summary>
/// The system's memory-pressure tier, as the platform reports it.
/// </summary>
/// <remarks>
/// Once the input to a residency policy that unloaded the engine when memory ran short. That
/// policy went with upstream 0.9.0: reloading the model costs a fraction of a second and a few
/// hundred megabytes, which is not a decision worth negotiating with the OS about, and every
/// one of those mechanisms was a way for dictation to behave differently on two consecutive
/// attempts. The tier is still reported by <see cref="ISystemEventSource"/> and read by nothing;
/// the one control left is the person's own, <see cref="IdleUnloadPolicy"/>.
/// </remarks>
public enum MemoryPressureTier
{
    Normal,
    Warning,
    Critical,
}
