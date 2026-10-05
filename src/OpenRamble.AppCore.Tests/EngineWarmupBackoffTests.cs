namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The retry ladder behind "preparation never gives up". Upstream pins it through the app-state
/// suites (FirstLoadNeverStrandsTests); here the ladder is checked on its own.
/// </summary>
public sealed class EngineWarmupBackoffTests
{
    /// <summary>The first attempts are for a hiccup, the tail for a genuinely busy machine.</summary>
    [Fact]
    public void TestTheLadderClimbsAndThenHolds()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), EngineWarmupBackoff.Delay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), EngineWarmupBackoff.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), EngineWarmupBackoff.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(10), EngineWarmupBackoff.Delay(3));
        Assert.Equal(TimeSpan.FromSeconds(30), EngineWarmupBackoff.Delay(4));
        Assert.Equal(TimeSpan.FromSeconds(30), EngineWarmupBackoff.Delay(5));
        Assert.Equal(TimeSpan.FromSeconds(30), EngineWarmupBackoff.Delay(1_000));
    }

    /// <summary>A negative count is nobody's failure yet: the first rung.</summary>
    [Fact]
    public void TestBeforeAnyFailureItIsTheFirstRung()
    {
        Assert.Equal(EngineWarmupBackoff.Ladder[0], EngineWarmupBackoff.Delay(-1));
    }
}
