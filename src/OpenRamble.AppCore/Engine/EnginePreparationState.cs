namespace OpenRamble.AppCore;

/// <summary>
/// What to show while the engine is preparing for the first dictation.
///
/// The first launch after installation was silent for tens of seconds, and all that time the
/// person did not understand whether something was broken.
///
/// <b>There is no progress bar here and there never will be.</b> No load-progress signal exists -
/// neither from the system nor from the library. A drawn stripe would be fiction, and fictional
/// progress is worse than honest silence: it promises a deadline nobody knows. We show the
/// elapsed seconds and explain the reason.
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
        Ready,
    }

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

    public static EnginePreparationState Make(Phase phase, TimeSpan elapsed)
    {
        int seconds = (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
        return phase switch
        {
            Phase.Idle => new EnginePreparationState(phase, elapsed, "Model not prepared", null),

            Phase.LoadingRecognizer => new EnginePreparationState(
                phase,
                elapsed,
                $"Preparing the recognizer… {seconds} s",
                // The formulation is correct on both a cold and a warm start, so no branches are
                // needed and the promise cannot be broken.
                "Windows is reading the model into memory and planning how to run it, then "
                    + "running one silent inference. Up to about 20–40 seconds the first time "
                    + "after install, a fraction of a second afterwards. There is no progress to "
                    + "show — the seconds are real."),

            Phase.LoadingVocabulary => new EnginePreparationState(
                phase,
                elapsed,
                $"Preparing the term booster… {seconds} s",
                "A second, smaller model. Same one-time load."),

            Phase.Ready => new EnginePreparationState(phase, elapsed, "Ready to dictate", null),

            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
    }
}
