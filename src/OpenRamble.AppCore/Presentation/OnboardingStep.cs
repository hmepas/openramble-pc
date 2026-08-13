using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// First launch steps and the rules for moving between them.
///
/// A separate type, and not a field inside the view: here it is decided whether a new user
/// reaches a working product or hits a disabled button. That has to be checked with a table,
/// not with one's eyes on a live screen - there may be no screen in the check at all.
/// </summary>
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

/// <summary>
/// Does the step let the person through and, if not, why.
///
/// The reason is not decoration. A dimmed button leaves a person guessing what is wanted from
/// them; for a blind person it is simply an "unavailable Continue button", and that is where
/// setup ends.
/// </summary>
/// <remarks>
/// The macOS original gates on two permissions - Microphone and Accessibility - and has a
/// branch for each of the four combinations. Windows needs no Accessibility grant, so one flag
/// and one sentence remain.
/// </remarks>
public static class OnboardingGate
{
    /// <summary><c>null</c> - you can move on.</summary>
    public static string? BlockReason(
        OnboardingStep step,
        bool microphoneGranted,
        ModelState modelState,
        bool engineReady = true,
        bool trialSucceeded = true)
    {
        switch (step)
        {
            case OnboardingStep.Welcome:
                return null;

            case OnboardingStep.TryIt:
                return trialSucceeded ? null : "Try dictation first, or press “Skip the try-out”.";

            case OnboardingStep.Setup:
                // The permission and the local model are one setup job. Keeping them on separate
                // pages created an almost empty "Model ready" step for existing installs and made
                // the shortest path feel longer than it is.
                if (!microphoneGranted) { return "Microphone is still needed."; }

                return modelState switch
                {
                    ModelState.Ready => engineReady
                        ? null
                        : "Wait for the model to finish preparing for first use.",
                    ModelState.NotInstalled =>
                        "Download the model first — without it there is nothing to recognize with.",
                    ModelState.Downloading => "Wait for the download to finish.",
                    ModelState.Verifying => "The download is being verified.",
                    ModelState.RepairRequired => "The model is damaged. Redownload it explicitly.",
                    ModelState.Failed => "The download failed. Try again.",
                    ModelState.Deleting => "The model is being deleted.",
                    _ => throw new ArgumentOutOfRangeException(nameof(modelState)),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    public static bool CanAdvance(
        OnboardingStep step,
        bool microphoneGranted,
        ModelState modelState,
        bool engineReady = true,
        bool trialSucceeded = true) =>
        BlockReason(step, microphoneGranted, modelState, engineReady, trialSucceeded) is null;
}
