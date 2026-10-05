using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The step the setup gate now opens onto, while the first load is still running.
///
/// Removing the engine from the setup gate was right - nothing was preparing an engine the
/// screen was waiting for - but it moved the person one screen further along, onto a step whose
/// entire instruction is "hold the key". For the one-time load after a fresh install that press
/// is refused: recording never starts, and the person is left with a screen that says "hold the
/// key, say a few words" and a footer that says "try dictation first". Both sentences are false
/// for as long as the load lasts.
///
/// This is not a deadlock - the load ends by itself, and "Skip the try-out" is right there - so
/// the fix is not another gate. It is the second half of the bar: a screen held by work in
/// motion has to say truthfully what that work is. The words already exist, in the sentence the
/// key press itself produces, so the screen says that one rather than inventing a second opinion.
/// </summary>
public sealed class TryOutStepTests
{
    private static readonly ModelState Ready = new ModelState.Ready("/tmp/model");

    private const string FirstLoad =
        "The model is getting ready for this PC — usually 20–40 seconds, and only once.";

    // MARK: - The footer says what the key press would say

    [Fact]
    public void TheTryOutFooterNamesTheLoadThatIsRefusingTheKeyPress()
    {
        Assert.Equal(
            FirstLoad,
            OnboardingGate.BlockReason(
                OnboardingStep.TryIt,
                new OnboardingConditions(true, Ready, trialSucceeded: false, dictationRefusal: FirstLoad)));
    }

    /// <summary>
    /// Once a take has landed, a cold engine is nothing to mention.
    ///
    /// The idle rule may give the memory back between the trial and the button, and that
    /// engine's comeback really is the next key press. Naming it there would be the resting
    /// engine's own false alarm, one screen later.
    /// </summary>
    [Fact]
    public void ATrialThatAlreadySucceededIsNotHeldByAnythingAtAll()
    {
        Assert.Null(
            OnboardingGate.BlockReason(
                OnboardingStep.TryIt,
                new OnboardingConditions(true, Ready, trialSucceeded: true, dictationRefusal: FirstLoad)));
    }

    /// <summary>With the engine loaded, the step asks for the one thing it is for.</summary>
    [Fact]
    public void WithoutARefusalTheStepStillAsksForTheTrial()
    {
        Assert.Equal(
            "Try dictation first, or press \u201CSkip the try-out\u201D.",
            OnboardingGate.BlockReason(
                OnboardingStep.TryIt,
                new OnboardingConditions(true, Ready, trialSucceeded: false)));
    }

    // MARK: - It is a sentence, never a gate

    /// <summary>
    /// Nothing here may decide whether a button is alive.
    ///
    /// This is the whole difference between saying what is happening and waiting for it. The
    /// engine requirement stranded two upstream releases because it could disable Continue; a
    /// refusal that only ever replaces one sentence with a truer one cannot strand anything.
    /// Checked for every step and every combination, so it stays that way.
    /// </summary>
    [Fact]
    public void AKeyPressRefusalNeverChangesWhetherAStepCanAdvance()
    {
        ModelState[] states = [Ready, new ModelState.NotInstalled(), new ModelState.Downloading(1, 2)];
        foreach (OnboardingStep step in OnboardingStepInfo.AllCases)
        {
            foreach (bool microphone in new[] { true, false })
            {
                foreach (ModelState model in states)
                {
                    foreach (bool trial in new[] { true, false })
                    {
                        bool Advance(string? refusal) => OnboardingGate.CanAdvance(
                            step,
                            new OnboardingConditions(microphone, model, trialSucceeded: trial, dictationRefusal: refusal));

                        Assert.True(
                            Advance(FirstLoad) == Advance(null),
                            $"step={step} microphone={microphone} model={model} trial={trial}: "
                            + "a refused key press changed whether the button is alive.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The setup step never hears about the engine again.
    ///
    /// It is the screen from the photograph, and the requirement that stranded it is gone. A
    /// refusal reaching it would put it straight back - this time through a field rather than a
    /// parameter.
    /// </summary>
    [Fact]
    public void TheSetupStepIsNeverHeldByARefusedKeyPress()
    {
        Assert.Null(
            OnboardingGate.BlockReason(
                OnboardingStep.Setup,
                new OnboardingConditions(true, Ready, dictationRefusal: FirstLoad)));
    }
}
