namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The cold-suspicion rule that decides whether a keypress warms the engine under the person's
/// voice.
/// </summary>
public class EngineWarmingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);

    /// <summary>No recorded activity means the engine's warmth is unknown — warm it.</summary>
    [Fact]
    public void UnknownActivityWarms()
    {
        Assert.True(EngineWarming.ShouldWarm(null, Now));
    }

    /// <summary>Active dictation flow never pays for pings.</summary>
    [Fact]
    public void RecentActivitySkipsTheWarmup()
    {
        Assert.False(EngineWarming.ShouldWarm(Now.AddSeconds(-30), Now));
        Assert.False(EngineWarming.ShouldWarm(Now.AddSeconds(-299), Now));
    }

    /// <summary>A return from real idle — where eviction happens — warms.</summary>
    [Fact]
    public void LongIdleWarms()
    {
        Assert.True(EngineWarming.ShouldWarm(Now.AddSeconds(-300), Now));
        Assert.True(EngineWarming.ShouldWarm(Now.AddSeconds(-28_800), Now));
    }
}
