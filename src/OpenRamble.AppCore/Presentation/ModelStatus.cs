using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// What a person sees about the model - in any of its states and on both screens.
///
/// One type for onboarding and settings deliberately: these states used to be written in two
/// places with different words, and any change had to be entered twice. The second place sooner
/// or later fell behind.
/// </summary>
public sealed record ModelStatus
{
    /// <summary>
    /// The full download, in megabytes as the file manager counts them.
    ///
    /// 670, not the original's 586: this port installs one model, not two, and it is the ONNX
    /// export rather than the Core ML bundle. The number is the bundled manifest's own total, so
    /// it moves with the manifest instead of being remembered here.
    /// </summary>
    public const int FullDownloadMegabytes = 670;

    public enum Tone
    {
        Neutral,
        Success,
        Failure,
    }

    public enum Action
    {
        Install,
        Retry,
        Repair,
        Cancel,
        Delete,
    }

    /// <summary>Where it is shown - only the set of buttons depends on this.</summary>
    public enum Place
    {
        Onboarding,
        Settings,
    }

    public required string Title { get; init; }

    public string? Detail { get; init; }

    /// <summary>Fraction complete, if meaningful.</summary>
    public double? Progress { get; init; }

    /// <summary>The caption for the indicator is also the value for a screen reader.</summary>
    public string? ProgressLabel { get; init; }

    public required IReadOnlyList<Action> Actions { get; init; }

    public required Tone ToneValue { get; init; }

    /// <summary>What to announce to a screen reader when the state changes.</summary>
    public required string Announcement { get; init; }

    /// <summary>How much the install/repair button will download - the full volume or the remainder.</summary>
    public int DownloadMegabytes { get; init; } = FullDownloadMegabytes;

    public string TitleFor(Action action) => TitleOf(action, DownloadMegabytes);

    public string HintFor(Action action) => HintOf(action, DownloadMegabytes);

    /// <summary>
    /// The button names the actual volume: full for a clean installation and only the remainder
    /// when part of it is already on disk.
    /// </summary>
    public static string TitleOf(Action action, int downloadMegabytes) => action switch
    {
        Action.Install => $"Download Model — {downloadMegabytes} MB",
        Action.Retry => "Try Again",
        Action.Repair => $"Redownload Model — {downloadMegabytes} MB",
        Action.Cancel => "Cancel Download",
        Action.Delete => "Delete Model",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Screen-reader hint: what will happen when you press it.</summary>
    public static string HintOf(Action action, int downloadMegabytes) => action switch
    {
        Action.Install => $"Downloads about {downloadMegabytes} MB. This is the app's only download.",
        Action.Retry => "Restarts the model download from the beginning.",
        Action.Repair => "Downloads and verifies a fresh copy of the model. The damaged copy is not used.",
        Action.Cancel => "Stops the download and deletes the partially downloaded files.",
        Action.Delete => "Frees up disk space. Dictation stops working until the model is downloaded again.",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static ModelStatus Make(
        ModelState state,
        bool isPreparingEngine,
        Place place,
        EnginePreparationState? preparation = null,
        int downloadMegabytes = FullDownloadMegabytes) =>
        MakeStatus(state, isPreparingEngine, preparation, place, downloadMegabytes) with
        {
            DownloadMegabytes = downloadMegabytes,
        };

    private static ModelStatus MakeStatus(
        ModelState state,
        bool isPreparingEngine,
        EnginePreparationState? preparation,
        Place place,
        int downloadMegabytes)
    {
        switch (state)
        {
            case ModelState.NotInstalled:
                return new ModelStatus
                {
                    Title = "Model not installed",
                    Detail = $"{downloadMegabytes} MB from the Hugging Face CDN; a GitHub mirror if "
                        + "it's unavailable. After verification, recognition works without the network.",
                    Actions = [Action.Install],
                    ToneValue = Tone.Neutral,
                    Announcement = "Model not installed",
                };

            case ModelState.Downloading downloading:
                return new ModelStatus
                {
                    Title = "Downloading model…",
                    // The same fact serves two different moments: in onboarding the person is
                    // mid-checklist and the download must not read as a blocker; in settings they
                    // are just visiting.
                    Detail = place == Place.Onboarding
                        ? "Keep going — grant the permission below while it downloads."
                        : "You can keep working — the download won't be interrupted.",
                    Progress = state.Progress,
                    ProgressLabel = $"{Megabytes(downloading.ReceivedBytes)} of {Megabytes(downloading.TotalBytes)} MB",
                    Actions = [Action.Cancel],
                    ToneValue = Tone.Neutral,
                    // Exact progress stays on the progress bar. Keeping the proactive
                    // announcement stable prevents a screen reader from speaking on every
                    // network progress callback.
                    Announcement = "Downloading model",
                };

            case ModelState.Verifying verifying:
                return new ModelStatus
                {
                    Title = "Verifying download…",
                    Detail = "Checking every file against its checksum.",
                    Progress = state.Progress,
                    ProgressLabel = $"File {verifying.Checked} of {verifying.Total}",
                    Actions = [],
                    ToneValue = Tone.Neutral,
                    Announcement = "Verifying download",
                };

            case ModelState.Ready:
                return new ModelStatus
                {
                    Title = "Model ready",
                    // While the first load into the engine is in progress the person sees
                    // "ready", but dictation will still think about it. Staying silent about that
                    // earns a complaint about being slow the first time. Live seconds, not
                    // "usually 20–40": a wait with a counter reads as work, without one it reads
                    // as stuck.
                    Detail = isPreparingEngine
                        ? preparation?.Title ?? "Preparing for this PC — usually 20–40 seconds, and only once."
                        : null,
                    Actions = place == Place.Settings ? [Action.Delete] : [],
                    ToneValue = Tone.Success,
                    Announcement = isPreparingEngine ? "Model ready, preparing for first use" : "Model ready",
                };

            case ModelState.RepairRequired repairRequired:
                {
                    string reason = Message(new ModelStoreError.RepairRequired(repairRequired.Reason));
                    return new ModelStatus
                    {
                        Title = "Model needs repair",
                        Detail = reason,
                        Actions = [Action.Repair],
                        ToneValue = Tone.Failure,
                        Announcement = $"Model needs repair. {reason}",
                    };
                }

            case ModelState.Failed failed:
                {
                    string reason = Message(failed.Error);
                    bool requiresRepair = failed.Error is ModelStoreError.RepairRequired;
                    return new ModelStatus
                    {
                        Title = requiresRepair ? "Model needs repair" : "Model installation failed",
                        Detail = reason,
                        Actions = [requiresRepair ? Action.Repair : Action.Retry],
                        ToneValue = Tone.Failure,
                        Announcement = requiresRepair
                            ? $"Model needs repair. {reason}"
                            : $"Model installation failed. {reason}",
                    };
                }

            case ModelState.Deleting:
                return new ModelStatus
                {
                    Title = "Deleting model…",
                    Actions = [],
                    ToneValue = Tone.Neutral,
                    Announcement = "Deleting model",
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    /// <summary>
    /// The error in human words.
    ///
    /// Previously the type's own description was printed here - that is, the person saw
    /// <c>notEnoughDiskSpace(requiredBytes: 594000000, availableBytes: 1200000)</c> and had to
    /// guess that the disk was full.
    /// </summary>
    public static string Message(ModelStoreError error) => error switch
    {
        ModelStoreError.NotEnoughDiskSpace space =>
            $"Not enough disk space: {Megabytes(space.RequiredBytes)} MB needed, "
                + $"{Megabytes(space.AvailableBytes)} MB free.",
        ModelStoreError.Download download => $"Download failed: {download.Reason}",
        ModelStoreError.Verification verification =>
            $"The download didn't match its checksums: {verification.Reason}",
        ModelStoreError.Install install => $"Couldn't put the files in place: {install.Reason}",
        ModelStoreError.RepairRequired repair =>
            $"The model is damaged or incomplete: {repair.Reason}. Redownload it explicitly.",
        ModelStoreError.Manifest manifest => $"The model's file list is corrupted: {manifest.Reason}",
        ModelStoreError.ImportSource source => $"That folder didn't work: {source.Reason}",
        ModelStoreError.Cancelled => "Download cancelled.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    /// <summary>Bytes to megabytes - as the file manager counts them.</summary>
    private static long Megabytes(long bytes) => bytes / 1_000_000;
}
