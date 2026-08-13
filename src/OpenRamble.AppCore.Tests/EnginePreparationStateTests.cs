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
            "Preparing the recognizer… 14 s",
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(14)).Title);
        Assert.Equal(
            "Preparing the term booster… 3 s",
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingVocabulary, TimeSpan.FromSeconds(3)).Title);
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

    /// <summary>Live seconds reach the line a person sees - otherwise a wait reads as a hang.</summary>
    [Fact]
    public void Scenario012()
    {
        ModelStatus status = ModelStatus.Make(
            new ModelState.Ready("/tmp"),
            isPreparingEngine: true,
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(9)));

        Assert.Equal("Preparing the recognizer… 9 s", status.Detail);
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
            isPreparingEngine: true,
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingVocabulary, TimeSpan.FromSeconds(2)));

        Assert.Equal("Preparing the term booster… 2 s", status.Detail);
    }

    /// <summary>The preparation is over - there is no line at all.</summary>
    [Fact]
    public void Scenario014()
    {
        ModelStatus status = ModelStatus.Make(
            new ModelState.Ready("/tmp"),
            isPreparingEngine: false,
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.Ready, TimeSpan.Zero));

        Assert.Null(status.Detail);
    }
}
