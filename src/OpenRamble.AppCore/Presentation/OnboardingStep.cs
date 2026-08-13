namespace OpenRamble.AppCore;

/// <summary>
/// First launch steps and the rules for moving between them.
///
/// A separate type, and not a field inside the view: here it is decided whether a new user
/// reaches a working product or hits a disabled button. That has to be checked with a table,
/// not with one's eyes on a live screen - there may be no screen in the check at all.
/// </summary>
/// <remarks>
/// <c>OnboardingGate</c> - the part that decides whether a step lets the person through - is
/// deferred: every one of its branches switches on <c>LocalASR.ModelState</c>.
/// </remarks>
public enum OnboardingStep
{
    Welcome = 0,
    Setup = 1,
    TryIt = 2,
}

public static class OnboardingStepInfo
{
    public static IReadOnlyList<OnboardingStep> AllCases { get; } =
        [OnboardingStep.Welcome, OnboardingStep.Setup, OnboardingStep.TryIt];

    extension(OnboardingStep step)
    {
        public OnboardingStep? Next => Step((int)step + 1);

        public OnboardingStep? Previous => Step((int)step - 1);

        /// <summary>
        /// Is there somewhere to return to?
        ///
        /// On the first step there is no "Back" button at all: it would lead nowhere, and a
        /// disabled button looks broken.
        /// </summary>
        public bool HasPrevious => step.Previous is not null;

        public bool IsLast => step.Next is null;

        /// <summary>Caption of the forward button.</summary>
        public string NextButtonTitle => step.IsLast ? "Done" : "Continue";

        public string ProgressText => $"{(int)step + 1} of {AllCases.Count}";

        /// <summary>
        /// The same thing in words.
        ///
        /// "1 of 3" without the word "step" is read by a screen reader as a couple of numbers
        /// out of nowhere.
        /// </summary>
        public string ProgressAccessibilityLabel => $"Step {(int)step + 1} of {AllCases.Count}";
    }

    private static OnboardingStep? Step(int rawValue) =>
        rawValue >= 0 && rawValue < AllCases.Count ? (OnboardingStep)rawValue : null;
}
