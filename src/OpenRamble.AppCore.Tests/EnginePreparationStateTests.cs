using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>Engine preparation state: honest seconds instead of fictitious progress.</summary>
public class EnginePreparationStateTests
{
    private static readonly EnginePreparationState.Phase[] AllPhases =
    [
        EnginePreparationState.Phase.Idle,
        EnginePreparationState.Phase.LoadingRecognizer,
        EnginePreparationState.Phase.LoadingVocabulary,
        EnginePreparationState.Phase.WarmingUp,
        EnginePreparationState.Phase.Ready,
    ];

    /// <summary>
    /// There is no load-progress signal. A drawn stripe would promise a deadline nobody knows.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        foreach (EnginePreparationState.Phase phase in AllPhases)
        {
            EnginePreparationState state = EnginePreparationState.Make(phase, TimeSpan.FromSeconds(7));
            Assert.DoesNotContain("%", state.Title);
            Assert.DoesNotContain("%", state.Detail ?? string.Empty);
            Assert.NotEmpty(state.Title);
        }
    }

    [Fact]
    public void Scenario008()
    {
        Assert.Equal(
            "Loading the recognizer… 14 s",
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(14)).Title);
        Assert.Equal(
            "Loading the term booster… 3 s",
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingVocabulary, TimeSpan.FromSeconds(3)).Title);
        Assert.Equal(
            "Warming up recognition… 2 s",
            EnginePreparationState.Make(EnginePreparationState.Phase.WarmingUp, TimeSpan.FromSeconds(2)).Title);
    }

    [Fact]
    public void Scenario009()
    {
        EnginePreparationState ready = EnginePreparationState.Make(EnginePreparationState.Phase.Ready, TimeSpan.Zero);

        Assert.Equal("Ready to dictate", ready.Title);
        Assert.Null(ready.Detail);
    }

    /// <summary>
    /// Engine failure is not part of this type: it already has an owner - the model's repair
    /// state. A second type would disagree with the first about the same thing.
    /// </summary>
    [Fact]
    public void Scenario010()
    {
        foreach (EnginePreparationState.Phase phase in AllPhases)
        {
            EnginePreparationState state = EnginePreparationState.Make(phase, TimeSpan.FromSeconds(1));
            Assert.DoesNotContain("fail", state.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("error", state.Title, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The Windows copy names what is actually happening. "Neural Engine" would be a lie about a
    /// machine that has none.
    /// </summary>
    [Fact]
    public void PreparationCopyDoesNotPromiseAppleHardware()
    {
        string? detail = EnginePreparationState
            .Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(1))
            .Detail;

        Assert.NotNull(detail);
        Assert.DoesNotContain("Neural Engine", detail);
        Assert.DoesNotContain("macOS", detail);
    }

    /// <summary>The bar advances by completed steps, never by a made-up percentage.</summary>
    [Fact]
    public void TheStepIsTheMilestoneThatIsRunning()
    {
        Assert.Equal(3, EnginePreparationState.StepCount);
        Assert.Equal(1, EnginePreparationState.Make(EnginePreparationState.Phase.Idle, TimeSpan.Zero).Step);
        Assert.Equal(1, EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.Zero).Step);
        Assert.Equal(2, EnginePreparationState.Make(EnginePreparationState.Phase.LoadingVocabulary, TimeSpan.Zero).Step);
        Assert.Equal(3, EnginePreparationState.Make(EnginePreparationState.Phase.WarmingUp, TimeSpan.Zero).Step);
        Assert.Equal(3, EnginePreparationState.Make(EnginePreparationState.Phase.Ready, TimeSpan.Zero).Step);
    }

    /// <summary>Live seconds reach the line a person sees - otherwise a wait reads as a hang.</summary>
    [Fact]
    public void Scenario012()
    {
        ModelStatus status = ModelStatus.Make(
            new ModelState.Ready("/tmp"),
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(9)),
            isEngineReady: false,
            isPreparingEngine: true);

        Assert.Equal("Step 1 of 3 · Loading the recognizer… 9 s", status.ProgressLabel);
    }

    /// <summary>
    /// The second model is a separate phase: merging them into one line would show "a little
    /// more" at the moment new work started.
    /// </summary>
    [Fact]
    public void Scenario013()
    {
        ModelStatus status = ModelStatus.Make(
            new ModelState.Ready("/tmp"),
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingVocabulary, TimeSpan.FromSeconds(2)),
            isEngineReady: false,
            isPreparingEngine: true);

        Assert.Equal("Step 2 of 3 · Loading the term booster… 2 s", status.ProgressLabel);
        Assert.Equal(1.0 / 3.0, status.Progress ?? -1, 3);
    }

    /// <summary>The preparation is over - there is no line at all.</summary>
    [Fact]
    public void Scenario014()
    {
        ModelStatus status = ModelStatus.Make(
            new ModelState.Ready("/tmp"),
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.Ready, TimeSpan.Zero));

        Assert.Null(status.Detail);
        Assert.Null(status.ProgressLabel);
    }
}
