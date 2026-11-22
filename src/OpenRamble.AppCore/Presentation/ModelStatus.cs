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
    /// 720, not the original's 586: this port installs one model, not two, and it is our
    /// weight-only int4 ONNX build rather than the Core ML bundle. The number is the bundled
    /// manifest's own total, so it moves with the manifest instead of being remembered here.
    /// </summary>
    public const int FullDownloadMegabytes = 720;

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

    /// <summary>
    /// The whole card in one line, for the places that have room for one - the menu and the
    /// try-out step - or nothing at all.
    /// </summary>
    /// <remarks>
    /// A success tone means the model asks nothing of anyone: the files are usable and, whether
    /// the engine is loaded or resting, the next key press works. Everything else - missing,
    /// downloading, verifying, actually preparing, damaged, failing, being deleted - either
    /// needs the person or is visibly working, and says so.
    ///
    /// The menu used to write this line itself, from a ready model and a cold engine alone, and
    /// so it announced preparation of an engine residency had put to rest with nothing running.
    /// That is the setup screen's own bug on a second surface. One author for the sentence, and
    /// there is no second opinion to drift.
    /// </remarks>
    public string? SetupLine =>
        ToneValue == Tone.Success
            ? null
            : ProgressLabel is string label ? $"{Title} — {label}" : Title;

    public string TitleFor(Action action) => TitleOf(action, DownloadMegabytes);

    public string HintFor(Action action) =>
        action == Action.Retry && RetriesLoadOnly
            ? "Tries starting the speech engine again with the installed model. Nothing is downloaded."
            : HintOf(action, DownloadMegabytes);

    /// <summary>
    /// The failure was the engine's runtime, not the files: "Try Again" reloads the model already
    /// on disk instead of downloading it.
    /// </summary>
    private bool RetriesLoadOnly { get; init; }

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

    /// <summary>
    /// The card reads three facts, and never guesses: is the recognizer usable, is preparation
    /// actually running right now, and how far along it is. Claiming "preparing" from readiness
    /// alone is what once left a fresh install staring at a promise nothing was keeping.
    /// </summary>
    public static ModelStatus Make(
        ModelState state,
        Place place,
        EnginePreparationState? preparation = null,
        int downloadMegabytes = FullDownloadMegabytes,
        bool isEngineReady = true,
        bool isPreparingEngine = false) =>
        MakeStatus(state, preparation, place, downloadMegabytes, isEngineReady, isPreparingEngine) with
        {
            DownloadMegabytes = downloadMegabytes,
        };

    private static ModelStatus MakeStatus(
        ModelState state,
        EnginePreparationState? preparation,
        Place place,
        int downloadMegabytes,
        bool isEngineReady,
        bool isPreparingEngine)
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
                // Three honest states, never blurred: preparing (with real steps), resting (given
                // back on purpose, comes back on the next press), and ready. Saying "preparing"
                // without work behind it is the sentence that made a first run feel broken.
                if (!isEngineReady && isPreparingEngine)
                {
                    int step = preparation?.Step ?? 1;
                    const int total = EnginePreparationState.StepCount;
                    return new ModelStatus
                    {
                        Title = "Preparing the model",
                        // Live seconds, not "usually 20–40": a wait with a moving counter reads
                        // as work, without one it reads as stuck.
                        Detail = preparation?.Detail
                            ?? "The model is being read into memory and planned for this PC. This happens once.",
                        Progress = (double)(step - 1) / total,
                        ProgressLabel = preparation is null
                            ? $"Step {step} of {total}"
                            : $"Step {step} of {total} · {preparation.Title}",
                        Actions = place == Place.Settings ? [Action.Delete] : [],
                        ToneValue = Tone.Neutral,
                        Announcement = $"Preparing the model, step {step} of {total}",
                    };
                }

                if (!isEngineReady)
                {
                    return new ModelStatus
                    {
                        Title = "Model ready",
                        Detail = "The model rests until your next dictation, then loads in a moment.",
                        Actions = place == Place.Settings ? [Action.Delete] : [],
                        ToneValue = Tone.Success,
                        Announcement = "Model ready, resting",
                    };
                }

                return new ModelStatus
                {
                    Title = "Model ready",
                    Detail = null,
                    Actions = place == Place.Settings ? [Action.Delete] : [],
                    ToneValue = Tone.Success,
                    Announcement = "Model ready",
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
                    bool engineUnavailable = failed.Error is ModelStoreError.EngineUnavailable;
                    return new ModelStatus
                    {
                        Title = requiresRepair ? "Model needs repair"
                            : engineUnavailable ? "The speech engine didn't start"
                            : "Model installation failed",
                        Detail = reason,
                        Actions = [requiresRepair ? Action.Repair : Action.Retry],
                        RetriesLoadOnly = engineUnavailable,
                        ToneValue = Tone.Failure,
                        Announcement = requiresRepair
                            ? $"Model needs repair. {reason}"
                            : engineUnavailable
                                ? $"The speech engine didn't start. {reason}"
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
    /// The card for the newer model while the previous one still serves.
    ///
    /// Every state here leaves dictation working on the model already installed, and the card
    /// says so: the download is an offer, not a repair. Ported from upstream's
    /// <c>ModelStatus.upgrade</c>.
    /// </summary>
    public static ModelStatus Upgrade(ModelState state, int downloadMegabytes)
    {
        ModelStatus status = state switch
        {
            ModelState.Downloading downloading => new ModelStatus
            {
                Title = "Downloading Parakeet Ultra…",
                Detail = "Dictation keeps using the current model until the new one is ready.",
                Progress = state.Progress,
                ProgressLabel = $"{Megabytes(downloading.ReceivedBytes)} of {Megabytes(downloading.TotalBytes)} MB",
                Actions = [Action.Cancel],
                ToneValue = Tone.Neutral,
                Announcement = "Downloading Parakeet Ultra",
            },
            ModelState.Verifying => new ModelStatus
            {
                Title = "Checking Parakeet Ultra…",
                Progress = state.Progress,
                Actions = [],
                ToneValue = Tone.Neutral,
                Announcement = "Checking Parakeet Ultra",
            },
            ModelState.Failed or ModelState.RepairRequired => new ModelStatus
            {
                Title = "Parakeet Ultra didn't download",
                Detail = "Dictation keeps using the current model. Nothing was changed.",
                Actions = [Action.Retry],
                ToneValue = Tone.Failure,
                Announcement = "Parakeet Ultra didn't download",
            },
            _ => new ModelStatus
            {
                Title = "Parakeet Ultra is available",
                Detail = "A newer version of the same model with fewer recognition mistakes in English and "
                    + "Russian, run by transcribe.cpp. Dictation keeps working while it downloads, and the "
                    + "current model is removed once Ultra is ready.",
                Actions = [Action.Install],
                ToneValue = Tone.Neutral,
                Announcement = "Parakeet Ultra is available",
            },
        };
        return status with { DownloadMegabytes = downloadMegabytes };
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
        ModelStoreError.EngineUnavailable engine =>
            $"The model is installed, but {engine.Reason}. Downloading it again won't fix this.",
        ModelStoreError.Cancelled => "Download cancelled.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    /// <summary>Bytes to megabytes - as the file manager counts them.</summary>
    private static long Megabytes(long bytes) => bytes / 1_000_000;
}
