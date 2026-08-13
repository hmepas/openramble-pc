using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Onboarding is the only way for a new person to reach a working product.
/// </summary>
/// <remarks>
/// The macOS file also checks <c>OnboardingGate</c> - who is let through to the next step. Every
/// one of those scenarios switches on <c>LocalASR.ModelState</c>, so the gate and its ten
/// scenarios are deferred to the wave that brings the engine. What survives is the step ladder
/// itself, which is pure.
/// </remarks>
public sealed class OnboardingStepTests
{
    [Fact]
    public void Scenario001()
    {
        Assert.Equal(3, OnboardingStepInfo.AllCases.Count);
        Assert.Equal(OnboardingStep.Setup, OnboardingStep.Welcome.Next);
        Assert.Equal(OnboardingStep.TryIt, OnboardingStep.Setup.Next);
        Assert.Null(OnboardingStep.TryIt.Next);
    }

    /// <summary>
    /// There is no "Back" button at all on the first step.
    ///
    /// A disabled button looks broken, and pressing it would lead nowhere.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        Assert.False(OnboardingStep.Welcome.HasPrevious);
        Assert.Null(OnboardingStep.Welcome.Previous);

        Assert.True(OnboardingStep.Setup.HasPrevious);
        Assert.Equal(OnboardingStep.Welcome, OnboardingStep.Setup.Previous);
        Assert.Equal(OnboardingStep.Setup, OnboardingStep.TryIt.Previous);
    }

    [Fact]
    public void Scenario003()
    {
        Assert.Equal("Continue", OnboardingStep.Welcome.NextButtonTitle);
        Assert.Equal("Continue", OnboardingStep.Setup.NextButtonTitle);
        Assert.Equal("Done", OnboardingStep.TryIt.NextButtonTitle);
    }

    [Fact]
    public void Scenario004()
    {
        Assert.Equal("1 of 3", OnboardingStep.Welcome.ProgressText);
        Assert.Equal("3 of 3", OnboardingStep.TryIt.ProgressText);
        // "1 of 3" without the word "step" is read by a screen reader as a couple of numbers out
        // of nowhere.
        Assert.Equal("Step 1 of 3", OnboardingStep.Welcome.ProgressAccessibilityLabel);
        Assert.Equal("Step 3 of 3", OnboardingStep.TryIt.ProgressAccessibilityLabel);
    }
}
