using LocalASR;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Onboarding is the only way for a new person to reach a working product.
///
/// What is checked is not that the step switched, but what a person sees at it: will the button
/// let them through, and will they be told what is missing.
/// </summary>
/// <remarks>
/// The macOS file's permission scenarios cover two grants; Windows has one, so its
/// "two permissions left" and "Accessibility is still needed" branches have no counterpart here.
/// </remarks>
public sealed class OnboardingStepTests
{
    private static readonly ModelState Ready = new ModelState.Ready("/tmp/model");

    private static string? Reason(
        OnboardingStep step,
        bool microphone = true,
        ModelState? model = null,
        bool engineReady = true,
        bool trialSucceeded = true) =>
        OnboardingGate.BlockReason(step, microphone, model ?? Ready, engineReady, trialSucceeded);

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

    // MARK: - Who is let through

    [Fact]
    public void Scenario005()
    {
        Assert.Null(Reason(OnboardingStep.Welcome, microphone: false, model: new ModelState.NotInstalled()));
    }

    [Fact]
    public void Scenario006()
    {
        Assert.Equal(
            "Try dictation first, or press \u201CSkip the try-out\u201D.",
            Reason(OnboardingStep.TryIt, trialSucceeded: false));
        Assert.Null(Reason(OnboardingStep.TryIt, trialSucceeded: true));
    }

    // MARK: - Permission

    [Fact]
    public void Scenario008()
    {
        Assert.Equal("Microphone is still needed.", Reason(OnboardingStep.Setup, microphone: false));
    }

    /// <summary>
    /// The macOS original names Accessibility here. Windows needs no such grant, so the microphone
    /// is the only thing that can be missing - and once it is granted the permission block is over.
    /// </summary>
    [Fact]
    public void Scenario009()
    {
        Assert.Null(Reason(OnboardingStep.Setup, microphone: true));
    }

    [Fact]
    public void Scenario010()
    {
        Assert.Null(Reason(OnboardingStep.Setup, microphone: true));
        Assert.True(OnboardingGate.CanAdvance(OnboardingStep.Setup, microphoneGranted: true, modelState: Ready));
    }

    // MARK: - Model

    [Fact]
    public void Scenario011()
    {
        Assert.Equal(
            "Download the model first \u2014 without it there is nothing to recognize with.",
            Reason(OnboardingStep.Setup, model: new ModelState.NotInstalled()));
        Assert.Equal(
            "Wait for the download to finish.",
            Reason(OnboardingStep.Setup, model: new ModelState.Downloading(1, 2)));
        Assert.Equal(
            "The download is being verified.",
            Reason(OnboardingStep.Setup, model: new ModelState.Verifying(1, 12)));
        Assert.Equal(
            "The download failed. Try again.",
            Reason(OnboardingStep.Setup, model: new ModelState.Failed(new ModelStoreError.Download("no network"))));
        Assert.Equal(
            "The model is being deleted.",
            Reason(OnboardingStep.Setup, model: new ModelState.Deleting()));
    }

    [Fact]
    public void Scenario012()
    {
        Assert.Null(Reason(OnboardingStep.Setup, model: Ready));
    }

    [Fact]
    public void Scenario013()
    {
        Assert.Equal(
            "Wait for the model to finish preparing for first use.",
            Reason(OnboardingStep.Setup, model: Ready, engineReady: false));
    }

    /// <summary>
    /// At no point is a disabled button left without an explanation.
    ///
    /// This is exactly the dead end: a person sees a dead "Continue" and does not know what is
    /// wanted from them - and a blind person does not even see that much.
    /// </summary>
    [Fact]
    public void Scenario014()
    {
        ModelState[] states =
        [
            new ModelState.NotInstalled(),
            new ModelState.Downloading(0, 1),
            new ModelState.Verifying(0, 1),
            new ModelState.RepairRequired("damaged"),
            new ModelState.Failed(new ModelStoreError.Cancelled()),
            new ModelState.Deleting(),
            Ready,
        ];

        foreach (OnboardingStep step in OnboardingStepInfo.AllCases)
        {
            foreach (bool microphone in new[] { true, false })
            {
                foreach (ModelState model in states)
                {
                    bool blocked = !OnboardingGate.CanAdvance(step, microphone, model);
                    string? text = OnboardingGate.BlockReason(step, microphone, model);

                    Assert.Equal(blocked, !string.IsNullOrEmpty(text));
                }
            }
        }
    }
}
