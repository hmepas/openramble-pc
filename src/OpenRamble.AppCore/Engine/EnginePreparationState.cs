namespace OpenRamble.AppCore;

/// <summary>
/// What to show while the engine is preparing for the first dictation.
///
/// The first launch after installation was silent for tens of seconds, and all that time the
/// person did not understand whether something was broken.
///
/// <b>No invented percentage, ever.</b> No load-progress signal exists - neither from the system
/// nor from the library - so a smoothly filling bar would be fiction, and fictional progress is
/// worse than honest silence: it promises a deadline nobody knows.
///
/// What is real is the sequence. Preparation is three genuine milestones - the recognizer, the
/// term booster, and a real warm-up inference - and each one completing is a fact the app
/// observes. So the bar advances by completed steps and stands still inside a step, next to
/// seconds that keep moving. A person sees where they are without being told a number that was
/// made up.
///
/// Engine failure is deliberately not part of this type: it already has an owner -
/// <c>ModelStatus</c>'s repair state. A second type about the same thing would be a duplicate
/// that sooner or later disagrees with the first.
/// </summary>
/// <remarks>
/// <b>Deviation: the copy is rewritten, the state machine is not.</b> The macOS original narrates
/// Core ML compiling the model for the Neural Engine, which is an Apple-silicon story and would
/// be a lie on Windows. Here the same seconds are spent on something different and equally real:
/// ORT reads roughly 670 MB of weights off disk, builds the session's execution plan, and then
/// runs one silent inference so the first real dictation does not pay for it. The phases, the
/// refusal to fake progress and the "seconds are real" promise are unchanged.
///
/// <para>
/// <see cref="Phase.LoadingVocabulary"/> is kept and is currently unreachable from
/// <c>AppState</c>: the acoustic term hinter has no ONNX export (see
/// <see cref="ModelPairState"/>). Removing the phase would mean re-deriving it - and re-deciding
/// that the second model deserves its own line rather than an extended first one - the day the
/// export lands.
/// </para>
/// </remarks>
public sealed record EnginePreparationState
{
    public enum Phase
    {
        Idle,
        LoadingRecognizer,
        LoadingVocabulary,
        WarmingUp,
        Ready,
    }

    /// <summary>How many milestones preparation has. Shown as "Step N of 3".</summary>
    public const int StepCount = 3;

    private EnginePreparationState(Phase phase, TimeSpan elapsed, string title, string? detail)
    {
        PhaseValue = phase;
        Elapsed = elapsed;
        Title = title;
        Detail = detail;
    }

    /// <summary>Named <c>PhaseValue</c> because C# forbids a member with its nested type's name.</summary>
    public Phase PhaseValue { get; }

    public TimeSpan Elapsed { get; }

    public string Title { get; }

    public string? Detail { get; }

    /// <summary>
    /// Which milestone is running, 1-based. Idle has not started one yet and ready has finished
    /// them all; both clamp into range so the label is always sayable.
    /// </summary>
    public int Step => PhaseValue switch
    {
        Phase.Idle => 1,
        Phase.LoadingRecognizer => 1,
        Phase.LoadingVocabulary => 2,
        Phase.WarmingUp => 3,
        Phase.Ready => StepCount,
        _ => throw new ArgumentOutOfRangeException(nameof(PhaseValue)),
    };

    public static EnginePreparationState Make(Phase phase, TimeSpan elapsed)
    {
        int seconds = (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
        return phase switch
        {
            Phase.Idle => new EnginePreparationState(phase, elapsed, "Model not prepared", null),

            Phase.LoadingRecognizer => new EnginePreparationState(
                phase,
                elapsed,
                $"Loading the recognizer… {seconds} s",
                // The formulation is correct on both a cold and a warm start, so no branches are
                // needed and the promise cannot be broken.
                "The model is being read into memory and the run is being planned for this PC. "
                    + "Up to about 20–40 seconds the first time after install, a fraction of a "
                    + "second afterwards."),

            Phase.LoadingVocabulary => new EnginePreparationState(
                phase,
                elapsed,
                $"Loading the term booster… {seconds} s",
                "A second, smaller model. Same one-time load."),

            Phase.WarmingUp => new EnginePreparationState(
                phase,
                elapsed,
                $"Warming up recognition… {seconds} s",
                "One silent recognition, so your first real dictation is fast."),

            Phase.Ready => new EnginePreparationState(phase, elapsed, "Ready to dictate", null),

            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
    }
}
