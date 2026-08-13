namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The automatic unload rules: critical overrides everything except live work; warning needs
/// persistence AND idle AND the post-load hold; a healthy machine never unloads.
/// </summary>
public class EngineResidencyPolicyTests
{
    private static EngineResidencyPolicy.Decision Decide(
        MemoryPressureTier tier,
        int warnings = 0,
        bool loaded = true,
        bool busy = false,
        double idle = 3600,
        double sinceLoad = 3600) =>
        EngineResidencyPolicy.Decide(
            tier,
            warnings,
            loaded,
            busy,
            TimeSpan.FromSeconds(idle),
            TimeSpan.FromSeconds(sinceLoad));

    /// <summary>
    /// A healthy machine keeps the engine warm forever — no time-based unload exists.
    /// Manufacturing cold starts would attack the identity.
    /// </summary>
    [Fact]
    public void NormalPressureNeverUnloads()
    {
        Assert.Equal(new EngineResidencyPolicy.Decision.Keep(), Decide(MemoryPressureTier.Normal, idle: 86_400));
    }

    [Fact]
    public void UnloadedEngineNeedsNoDecision()
    {
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Keep(),
            Decide(MemoryPressureTier.Critical, loaded: false));
    }

    /// <summary>Work in flight always wins; completion events re-evaluate.</summary>
    [Fact]
    public void BusyEngineIsNeverUnloaded()
    {
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Keep(),
            Decide(MemoryPressureTier.Critical, busy: true));
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Keep(),
            Decide(MemoryPressureTier.Warning, warnings: 5, busy: true));
    }

    /// <summary>
    /// Critical pressure overrides the post-load hysteresis: release as much as possible, as soon
    /// as safely idle.
    /// </summary>
    [Fact]
    public void CriticalUnloadsImmediatelyEvenInsideThePostLoadHold()
    {
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Unload(),
            Decide(MemoryPressureTier.Critical, idle: 0, sinceLoad: 5));
    }

    /// <summary>One warning blip does nothing — persistence is required.</summary>
    [Fact]
    public void SingleWarningEventIsIgnored()
    {
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Keep(),
            Decide(MemoryPressureTier.Warning, warnings: 1));
    }

    [Fact]
    public void PersistedWarningWithIdleAndExpiredHoldUnloads()
    {
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Unload(),
            Decide(MemoryPressureTier.Warning, warnings: 2, idle: 301, sinceLoad: 601));
    }

    /// <summary>
    /// Warning-tier hysteresis: a fresh load is held for ten minutes so an ambient-pressure
    /// machine cannot oscillate between load and unload.
    /// </summary>
    [Fact]
    public void WarningRespectsPostLoadHold()
    {
        var decision = Assert.IsType<EngineResidencyPolicy.Decision.CheckAgain>(
            Decide(MemoryPressureTier.Warning, warnings: 3, idle: 3600, sinceLoad: 60));

        Assert.Equal(540, decision.After.TotalSeconds, 1);
    }

    /// <summary>Not idle long enough: the boundary is the remaining idle window.</summary>
    [Fact]
    public void WarningWaitsForTheIdleWindow()
    {
        var decision = Assert.IsType<EngineResidencyPolicy.Decision.CheckAgain>(
            Decide(MemoryPressureTier.Warning, warnings: 2, idle: 100, sinceLoad: 3600));

        Assert.Equal(200, decision.After.TotalSeconds, 1);
    }

    /// <summary>Both windows pending: one timer, armed for the LATER boundary.</summary>
    [Fact]
    public void PendingBoundaryIsTheLaterOfTheTwoWindows()
    {
        var decision = Assert.IsType<EngineResidencyPolicy.Decision.CheckAgain>(
            Decide(MemoryPressureTier.Warning, warnings: 2, idle: 250, sinceLoad: 60));

        Assert.Equal(540, decision.After.TotalSeconds, 1);
    }

    /// <summary>
    /// "Never active" arrives as <see cref="TimeSpan.MaxValue"/> from AppState. Swift's
    /// <c>.infinity</c> subtracts harmlessly; a TimeSpan does not, so the boundary maths must not
    /// overflow on it.
    /// </summary>
    [Fact]
    public void NeverActiveEngineDoesNotOverflowTheBoundaryMaths()
    {
        Assert.Equal(
            new EngineResidencyPolicy.Decision.Unload(),
            EngineResidencyPolicy.Decide(
                MemoryPressureTier.Warning,
                consecutiveWarnings: 2,
                engineLoaded: true,
                engineBusy: false,
                idleFor: TimeSpan.MaxValue,
                sinceLoadCompleted: TimeSpan.MaxValue));

        var decision = Assert.IsType<EngineResidencyPolicy.Decision.CheckAgain>(
            EngineResidencyPolicy.Decide(
                MemoryPressureTier.Warning,
                consecutiveWarnings: 2,
                engineLoaded: true,
                engineBusy: false,
                idleFor: TimeSpan.MaxValue,
                sinceLoadCompleted: TimeSpan.FromSeconds(60)));
        Assert.Equal(540, decision.After.TotalSeconds, 1);
    }
}
