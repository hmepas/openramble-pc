namespace OpenRamble.AppCore.Tests;

/// <summary>The Handy-style "Unload Model" row: seven options, five-minute default.</summary>
public sealed class IdleUnloadPolicyTests
{
    [Fact]
    public void TestDelaysMatchTheirLabels()
    {
        Assert.Null(IdleUnloadPolicy.Never.IdleDelay);
        Assert.Equal(TimeSpan.Zero, IdleUnloadPolicy.Immediately.IdleDelay);
        Assert.Equal(TimeSpan.FromSeconds(120), IdleUnloadPolicy.AfterTwoMinutes.IdleDelay);
        Assert.Equal(TimeSpan.FromSeconds(300), IdleUnloadPolicy.AfterFiveMinutes.IdleDelay);
        Assert.Equal(TimeSpan.FromSeconds(600), IdleUnloadPolicy.AfterTenMinutes.IdleDelay);
        Assert.Equal(TimeSpan.FromSeconds(900), IdleUnloadPolicy.AfterFifteenMinutes.IdleDelay);
        Assert.Equal(TimeSpan.FromSeconds(3_600), IdleUnloadPolicy.AfterOneHour.IdleDelay);
        Assert.Equal(7, IdleUnloadPolicies.AllCases.Count);
    }

    [Fact]
    public void TestDefaultMatchesHandy()
    {
        Assert.Equal(IdleUnloadPolicy.AfterFiveMinutes, IdleUnloadPolicies.Default);
    }

    /// <summary>
    /// The stored value is a string in settings.json rather than a defaults suite, but the rule
    /// is the original's: garbage and absence both read as the default, never as "never".
    /// </summary>
    [Fact]
    public void TestStoredFallsBackToDefaultOnGarbageAndAbsence()
    {
        Assert.Equal(IdleUnloadPolicies.Default, IdleUnloadPolicies.Parse(null));
        Assert.Equal(IdleUnloadPolicies.Default, IdleUnloadPolicies.Parse("after-nine-lives"));
        Assert.Equal(IdleUnloadPolicy.AfterOneHour, IdleUnloadPolicies.Parse(IdleUnloadPolicy.AfterOneHour.RawValue));
    }

    /// <summary>Every option round-trips through its stored spelling.</summary>
    [Fact]
    public void TestEveryCaseSurvivesStorage()
    {
        foreach (IdleUnloadPolicy policy in IdleUnloadPolicies.AllCases)
        {
            Assert.Equal(policy, IdleUnloadPolicies.Parse(policy.RawValue));
            Assert.False(string.IsNullOrEmpty(policy.Label));
        }
    }
}
