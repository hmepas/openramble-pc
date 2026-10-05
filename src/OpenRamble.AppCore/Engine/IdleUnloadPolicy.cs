namespace OpenRamble.AppCore;

/// <summary>
/// When to give the engine's memory back after the person stops dictating.
///
/// The zero-settings critical-pressure eviction stays as the floor; this is the opt-out
/// good-citizen layer on top, mirroring Handy's "Unload Model" row down to the seven options.
/// The comeback is designed to be invisible: the key press starts the reload under the voice,
/// and stop waits it out instead of dropping the words.
/// </summary>
public enum IdleUnloadPolicy
{
    Never,
    Immediately,
    AfterTwoMinutes,
    AfterFiveMinutes,
    AfterTenMinutes,
    AfterFifteenMinutes,
    AfterOneHour,
}

/// <summary>What each option means, and how it is stored.</summary>
public static class IdleUnloadPolicies
{
    /// <summary>
    /// Handy's default too; a machine that dictates all day just re-arms the timer and never
    /// notices.
    /// </summary>
    public const IdleUnloadPolicy Default = IdleUnloadPolicy.AfterFiveMinutes;

    /// <summary>The settings key. The same spelling as the macOS defaults key.</summary>
    public const string SettingsKey = "modelUnloadTimeout";

    /// <summary>In the order the picker shows them.</summary>
    public static IReadOnlyList<IdleUnloadPolicy> AllCases { get; } =
    [
        IdleUnloadPolicy.Never,
        IdleUnloadPolicy.Immediately,
        IdleUnloadPolicy.AfterTwoMinutes,
        IdleUnloadPolicy.AfterFiveMinutes,
        IdleUnloadPolicy.AfterTenMinutes,
        IdleUnloadPolicy.AfterFifteenMinutes,
        IdleUnloadPolicy.AfterOneHour,
    ];

    /// <summary>
    /// Unknown or absent stored values fall back to the default - a settings file from the
    /// future must not turn into <see cref="IdleUnloadPolicy.Never"/> silently.
    /// </summary>
    public static IdleUnloadPolicy Parse(string? stored)
    {
        foreach (IdleUnloadPolicy candidate in AllCases)
        {
            if (string.Equals(candidate.RawValue, stored, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return Default;
    }

    extension(IdleUnloadPolicy policy)
    {
        /// <summary>
        /// <c>null</c> - keep forever; <see cref="TimeSpan.Zero"/> - right after the session
        /// ends; otherwise the idle time before the unload.
        /// </summary>
        public TimeSpan? IdleDelay => policy switch
        {
            IdleUnloadPolicy.Never => null,
            IdleUnloadPolicy.Immediately => TimeSpan.Zero,
            IdleUnloadPolicy.AfterTwoMinutes => TimeSpan.FromSeconds(2 * 60),
            IdleUnloadPolicy.AfterFiveMinutes => TimeSpan.FromSeconds(5 * 60),
            IdleUnloadPolicy.AfterTenMinutes => TimeSpan.FromSeconds(10 * 60),
            IdleUnloadPolicy.AfterFifteenMinutes => TimeSpan.FromSeconds(15 * 60),
            IdleUnloadPolicy.AfterOneHour => TimeSpan.FromSeconds(60 * 60),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

        public string Label => policy switch
        {
            IdleUnloadPolicy.Never => "Never",
            IdleUnloadPolicy.Immediately => "Immediately",
            IdleUnloadPolicy.AfterTwoMinutes => "After 2 minutes",
            IdleUnloadPolicy.AfterFiveMinutes => "After 5 minutes",
            IdleUnloadPolicy.AfterTenMinutes => "After 10 minutes",
            IdleUnloadPolicy.AfterFifteenMinutes => "After 15 minutes",
            IdleUnloadPolicy.AfterOneHour => "After 1 hour",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

        /// <summary>
        /// The stored spelling: the Swift case name, so a settings value reads the same on
        /// every platform.
        /// </summary>
        public string RawValue => policy switch
        {
            IdleUnloadPolicy.Never => "never",
            IdleUnloadPolicy.Immediately => "immediately",
            IdleUnloadPolicy.AfterTwoMinutes => "afterTwoMinutes",
            IdleUnloadPolicy.AfterFiveMinutes => "afterFiveMinutes",
            IdleUnloadPolicy.AfterTenMinutes => "afterTenMinutes",
            IdleUnloadPolicy.AfterFifteenMinutes => "afterFifteenMinutes",
            IdleUnloadPolicy.AfterOneHour => "afterOneHour",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }
}
