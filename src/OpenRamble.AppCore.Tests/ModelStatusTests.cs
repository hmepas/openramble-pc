using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// What a person sees about the model in each of its states.
///
/// There are seven states and two screens, and all of them used to be written out by hand in
/// both places. What is checked here is what is visible: title, explanation, indicator caption
/// and set of buttons.
/// </summary>
public class ModelStatusTests
{
    private static readonly ModelState Ready = new ModelState.Ready("/tmp/model");

    private static ModelStatus Status(
        ModelState state,
        ModelStatus.Place place = ModelStatus.Place.Settings,
        bool engineReady = true,
        bool preparing = false) =>
        ModelStatus.Make(state, place, isEngineReady: engineReady, isPreparingEngine: preparing);

    [Fact]
    public void Scenario001()
    {
        ModelStatus status = Status(new ModelState.NotInstalled());

        Assert.Equal("Model not installed", status.Title);
        Assert.Equal([ModelStatus.Action.Install], status.Actions);
        // One model, one button: the bundled manifest's own 720 MB.
        Assert.Contains("720 MB", status.Detail);
        Assert.Null(status.Progress);
    }

    [Fact]
    public void Scenario002()
    {
        ModelStatus status = Status(new ModelState.Downloading(120_000_000, 483_000_000));

        Assert.Equal("Downloading model…", status.Title);
        Assert.Equal("120 of 483 MB", status.ProgressLabel);
        Assert.Equal(0.248, status.Progress ?? 0, 2);
        // The only action is to honestly stop the download and remove the partial files.
        Assert.Equal([ModelStatus.Action.Cancel], status.Actions);
        Assert.Equal("Downloading model", status.Announcement);
    }

    [Fact]
    public void Scenario003()
    {
        ModelStatus status = Status(new ModelState.Verifying(3, 12));

        Assert.Equal("Verifying download…", status.Title);
        Assert.Equal("File 3 of 12", status.ProgressLabel);
        Assert.Equal(0.25, status.Progress ?? 0, 3);
        Assert.Empty(status.Actions);
        Assert.Equal("Verifying download", status.Announcement);
        Assert.Equal(
            status.Announcement,
            Status(new ModelState.Verifying(11, 12)).Announcement);
    }

    [Fact]
    public void Scenario004()
    {
        ModelStatus status = Status(Ready, place: ModelStatus.Place.Settings);

        Assert.Equal("Model ready", status.Title);
        Assert.Equal(ModelStatus.Tone.Success, status.ToneValue);
        Assert.Equal([ModelStatus.Action.Delete], status.Actions);
    }

    /// <summary>
    /// There is no delete button in onboarding. A person is installing the app for the first
    /// time; offering to demolish the download they just made is the one thing they definitely
    /// do not need.
    /// </summary>
    [Fact]
    public void Scenario005()
    {
        Assert.Empty(Status(Ready, place: ModelStatus.Place.Onboarding).Actions);
    }

    /// <summary>
    /// Downloaded is not the same as usable: while the engine is still loading for this PC the
    /// card says so plainly instead of claiming readiness the dictation cannot honour yet.
    /// Preparation shows real milestones, and only while it is genuinely running. Claiming it
    /// from readiness alone once stranded a fresh install.
    /// </summary>
    [Fact]
    public void Scenario006()
    {
        ModelStatus working = ModelStatus.Make(
            Ready,
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(7)),
            isEngineReady: false,
            isPreparingEngine: true);
        Assert.Equal("Preparing the model", working.Title);
        Assert.Equal(ModelStatus.Tone.Neutral, working.ToneValue);
        Assert.Equal("Step 1 of 3 · Loading the recognizer… 7 s", working.ProgressLabel);
        Assert.Equal(0, working.Progress ?? -1, 3);

        // Nothing running: the card must not narrate work that does not exist.
        ModelStatus resting = Status(Ready, engineReady: false);
        Assert.Equal("Model ready", resting.Title);
        Assert.Equal(ModelStatus.Tone.Success, resting.ToneValue);
        Assert.Contains("rests until your next dictation", resting.Detail);
    }

    [Fact]
    public void Scenario007()
    {
        ModelStatus status = Status(new ModelState.Failed(new ModelStoreError.Download("the server did not respond")));

        Assert.Equal("Model installation failed", status.Title);
        Assert.Equal(ModelStatus.Tone.Failure, status.ToneValue);
        Assert.Equal([ModelStatus.Action.Retry], status.Actions);
        Assert.Equal("Download failed: the server did not respond", status.Detail);
    }

    [Fact]
    public void Scenario008()
    {
        ModelStatus status = Status(new ModelState.RepairRequired("checksum mismatch"));

        Assert.Equal("Model needs repair", status.Title);
        Assert.Equal([ModelStatus.Action.Repair], status.Actions);
        Assert.Equal("Redownload Model — 720 MB", status.TitleFor(ModelStatus.Action.Repair));
        // A top-up after an update names only the remainder, not the full amount.
        Assert.Equal(
            "Redownload Model — 103 MB",
            ModelStatus.TitleOf(ModelStatus.Action.Repair, 103));
        Assert.Contains("damaged", status.Detail);
    }

    [Fact]
    public void Scenario009()
    {
        ModelStatus status = Status(new ModelState.Deleting());

        Assert.Equal("Deleting model…", status.Title);
        Assert.Empty(status.Actions);
        Assert.Null(status.Progress);
    }

    /// <summary>
    /// A full disk used to be an enum dump with raw byte counts: the person saw
    /// <c>notEnoughDiskSpace(requiredBytes: 594…, availableBytes: 1…)</c> and had to guess.
    /// </summary>
    [Fact]
    public void Scenario010()
    {
        string text = ModelStatus.Message(new ModelStoreError.NotEnoughDiskSpace(594_000_000, 120_000_000));

        Assert.Equal("Not enough disk space: 594 MB needed, 120 MB free.", text);
        Assert.DoesNotContain("requiredBytes", text);
    }

    [Fact]
    public void Scenario011()
    {
        ModelStoreError[] errors =
        [
            new ModelStoreError.Manifest("broken json"),
            new ModelStoreError.Download("no network"),
            new ModelStoreError.Verification("amount did not match"),
            new ModelStoreError.Install("no rights"),
            new ModelStoreError.RepairRequired("no marker"),
            new ModelStoreError.ImportSource("wrong folder"),
            new ModelStoreError.NotEnoughDiskSpace(1, 0),
            new ModelStoreError.Cancelled(),
        ];

        foreach (ModelStoreError error in errors)
        {
            string text = ModelStatus.Message(error);
            Assert.NotEmpty(text);
            Assert.DoesNotContain("(", text);
        }
    }

    [Fact]
    public void Scenario012()
    {
        ModelState[] states =
        [
            new ModelState.NotInstalled(),
            new ModelState.Downloading(0, 483_000_000),
            new ModelState.Verifying(0, 12),
            Ready,
            new ModelState.RepairRequired("damaged"),
            new ModelState.Failed(new ModelStoreError.Cancelled()),
            new ModelState.Deleting(),
        ];

        var announcements = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModelState state in states)
        {
            string announcement = Status(state).Announcement;
            Assert.NotEmpty(announcement);
            announcements.Add(announcement);
        }

        Assert.Equal(states.Length, announcements.Count);
    }

    [Fact]
    public void Scenario013()
    {
        foreach (ModelStatus.Action action in
                 new[] { ModelStatus.Action.Install, ModelStatus.Action.Retry, ModelStatus.Action.Repair, ModelStatus.Action.Delete })
        {
            Assert.NotEmpty(ModelStatus.TitleOf(action, 670));
            Assert.NotEmpty(ModelStatus.HintOf(action, 670));
        }

        // Deleting is the only irreversible action on the screen, and its cost must be said
        // before pressing.
        Assert.Contains("stops working", ModelStatus.HintOf(ModelStatus.Action.Delete, 670));
    }

    /// <summary>
    /// The downloading hint serves the moment: onboarding steers the person to the permission
    /// below; settings only reassures.
    /// </summary>
    [Fact]
    public void DownloadingDetailNamesTheNextStepOnlyDuringOnboarding()
    {
        var downloading = new ModelState.Downloading(1_000_000, 483_000_000);

        Assert.Equal(
            "Keep going — grant the permission below while it downloads.",
            ModelStatus.Make(downloading, ModelStatus.Place.Onboarding).Detail);
        Assert.Equal(
            "You can keep working — the download won't be interrupted.",
            ModelStatus.Make(downloading, ModelStatus.Place.Settings).Detail);
    }
}
