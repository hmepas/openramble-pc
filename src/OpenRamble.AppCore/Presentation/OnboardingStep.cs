using DictationCore;
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

/// <summary>
/// Which revision of the setup flow the app insists on.
/// </summary>
/// <remarks>
/// A person who finished revision N is not shown the flow again by any later build - an update
/// must never look like a first launch. Bump this number, and only this number, when a release
/// genuinely needs returning users to walk through setup once more (a new required permission,
/// a new model to fetch); the bump is the decision, made here, on purpose.
/// </remarks>
public static class OnboardingRevision
{
    public const int Current = 1;
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
/// Everything onboarding is allowed to wait for, in one value.
/// </summary>
/// <remarks>
/// <para>
/// The list is the whole safety property, so it is written down as a type rather than as a
/// parameter list that each caller fills in for itself. A screen may only be held by a fact
/// that is the person's to change - the permission, a download they start - or by work
/// genuinely in motion that ends on its own. A loaded engine is neither: the idle unload gives
/// its memory back on a schedule, and that engine's own comeback is the next key press, which
/// nobody makes while this very screen is holding them here. Upstream had it on this list
/// until 0.8.2, and the result was a setup screen that said "Model ready" and "Getting the model
/// ready…" at the same instant and never let go until the app was quit.
/// </para>
/// <para>
/// Anything added here has to meet that test first - and it is read off the running app in
/// exactly one place, <see cref="OnboardingConditions(AppState, bool)"/>, so the tests that hold
/// this bar cannot be looking at a different screen from the one the app draws.
/// </para>
/// </remarks>
public sealed record OnboardingConditions
{
    public OnboardingConditions(
        bool microphoneGranted,
        ModelState modelState,
        bool isDictationBusy = false,
        bool trialSucceeded = true,
        string? dictationRefusal = null)
    {
        MicrophoneGranted = microphoneGranted;
        ModelState = modelState;
        IsDictationBusy = isDictationBusy;
        TrialSucceeded = trialSucceeded;
        DictationRefusal = dictationRefusal;
    }

    /// <summary>The facts as the running app holds them - the one reader every screen shares.</summary>
    public OnboardingConditions(AppState state, bool trialSucceeded)
        : this(
            state.MicrophoneGranted,
            state.ModelStateValue,
            isDictationBusy: state.DictationStateValue != DictationState.Idle,
            trialSucceeded: trialSucceeded,
            dictationRefusal: DictationReadiness.Reason(
                state.MicrophoneGranted,
                state.ModelStateValue,
                state.IsEngineReady,
                state.HasEngineBeenReady))
    {
    }

    public bool MicrophoneGranted { get; }

    public ModelState ModelState { get; }

    /// <summary>
    /// A dictation started from the try-out step. It ends by itself or by the "Cancel Dictation"
    /// button that is on the same screen.
    /// </summary>
    public bool IsDictationBusy { get; }

    public bool TrialSucceeded { get; }

    /// <summary>
    /// Why a key press right now would be refused, in the words the press itself uses -
    /// <c>null</c> when dictation can start.
    /// </summary>
    /// <remarks>
    /// The one exception to the paragraph above, and it earns it by never disabling anything:
    /// it only ever replaces the try-out step's own sentence with a truer one. That step is
    /// already held by a trial that has not happened, and while the one-time load runs it
    /// cannot happen - asking for it then is asking for something the app is refusing. There is
    /// no second opinion to drift, because this is the sentence the refused press itself shows
    /// (<see cref="DictationReadiness"/>).
    /// </remarks>
    public string? DictationRefusal { get; }
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
    public static string? BlockReason(OnboardingStep step, OnboardingConditions conditions)
    {
        // While a take is running, Back, Next and Skip are all disabled - this is the sentence
        // under all of them. It outranks the step's own reason because it is the one thing the
        // person can act on right now.
        if (conditions.IsDictationBusy)
        {
            return "Finish or cancel the current dictation first.";
        }

        switch (step)
        {
            case OnboardingStep.Welcome:
                return null;

            case OnboardingStep.TryIt:
                if (conditions.TrialSucceeded) { return null; }
                // This step asks for a key press, so it is held by whatever holds that press.
                // After a fresh install that is the one-time load, and for its tens of seconds
                // "try dictation first" asks for something the app itself is refusing - the
                // person holds the key, nothing records, and the screen keeps asking. The load
                // ends by itself and "Skip the try-out" is on the same screen, so nothing here
                // is a deadlock; it only has to be true.
                if (conditions.DictationRefusal is string refusal) { return refusal; }
                return "Try dictation first, or press “Skip the try-out”.";

            case OnboardingStep.Setup:
                // The permission and the local model are one setup job. Keeping them on separate
                // pages created an almost empty "Model ready" step for existing installs and made
                // the shortest path feel longer than it is.
                if (!conditions.MicrophoneGranted) { return "Microphone is still needed."; }

                // Usable files are the whole model requirement. The gate is not told whether
                // the engine is loaded, and must not be: the engine is loaded and unloaded by
                // the idle rule, and a resting one waits for a key press that nobody makes on
                // this screen. Every input here either moves by itself or is the person's to
                // change.
                return conditions.ModelState switch
                {
                    ModelState.Ready => null,
                    ModelState.NotInstalled =>
                        "Download the model first — without it there is nothing to recognize with.",
                    ModelState.Downloading => "Wait for the download to finish.",
                    ModelState.Verifying => "The download is being verified.",
                    ModelState.RepairRequired => "The model is damaged. Redownload it explicitly.",
                    ModelState.Failed => "The download failed. Try again.",
                    ModelState.Deleting => "The model is being deleted.",
                    _ => throw new ArgumentOutOfRangeException(nameof(conditions)),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    public static bool CanAdvance(OnboardingStep step, OnboardingConditions conditions) =>
        BlockReason(step, conditions) is null;
}
