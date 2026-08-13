using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// Combining the states of two models into one that a person can see.
///
/// For a person, recognition and the acoustic hinting of terms are one "model": one download
/// button, one progress bar, one destiny. Inside there are two independent stores with their own
/// manifests, and the union rule is pure policy with tests, not logic in a view model.
/// </summary>
/// <remarks>
/// <b>Not reachable from this port's <c>AppState</c> yet, and that is deliberate.</b> The
/// acoustic term hinter is Parakeet CTC 110M in Core ML form, and no ONNX export of it exists -
/// <see cref="ModelManifest.BundledVocabulary"/> refuses rather than downloading 98 MB nothing
/// can read. The policy is ported unchanged so that the day the export appears the combination
/// rules are already here, checked, and identical to the macOS build's.
/// </remarks>
public static class ModelPairState
{
    /// <summary>
    /// Priority order: breakdown loudest, deletion and work next, readiness only when both are
    /// ready.
    /// </summary>
    public static ModelState Combine(
        ModelState main,
        ModelState vocabulary,
        long mainTotalBytes,
        long vocabularyTotalBytes,
        int mainFileCount,
        int vocabularyFileCount)
    {
        long totalBytes = mainTotalBytes + vocabularyTotalBytes;
        int totalFiles = mainFileCount + vocabularyFileCount;

        if (main is ModelState.Failed mainFailure) { return mainFailure; }
        if (vocabulary is ModelState.Failed vocabularyFailure) { return vocabularyFailure; }

        if (main is ModelState.RepairRequired mainRepair)
        {
            return new ModelState.RepairRequired($"recognition model: {mainRepair.Reason}");
        }

        if (vocabulary is ModelState.RepairRequired vocabularyRepair)
        {
            return new ModelState.RepairRequired($"vocabulary helper: {vocabularyRepair.Reason}");
        }

        if (main is ModelState.Deleting) { return new ModelState.Deleting(); }
        if (vocabulary is ModelState.Deleting) { return new ModelState.Deleting(); }

        // Installation proceeds sequentially: first the main one, then the hinter. Progress is
        // shared and does not jump back at the border between them.
        if (main is ModelState.Downloading mainDownloading)
        {
            return new ModelState.Downloading(mainDownloading.ReceivedBytes, totalBytes);
        }

        if (main is ModelState.Verifying mainVerifying)
        {
            return new ModelState.Verifying(mainVerifying.Checked, totalFiles);
        }

        if (vocabulary is ModelState.Downloading vocabularyDownloading)
        {
            return new ModelState.Downloading(mainTotalBytes + vocabularyDownloading.ReceivedBytes, totalBytes);
        }

        if (vocabulary is ModelState.Verifying vocabularyVerifying)
        {
            return new ModelState.Verifying(mainFileCount + vocabularyVerifying.Checked, totalFiles);
        }

        if (main is ModelState.Ready ready && vocabulary is ModelState.Ready)
        {
            return ready;
        }

        return new ModelState.NotInstalled();
    }

    /// <summary>
    /// How many bytes are left to download: the full amount for a clean installation, only the
    /// remainder for a top-up after an application update.
    /// </summary>
    public static long RemainingBytes(
        ModelState main,
        ModelState vocabulary,
        long mainTotalBytes,
        long vocabularyTotalBytes)
    {
        long bytes = 0;
        if (!main.IsReady) { bytes += mainTotalBytes; }
        if (!vocabulary.IsReady) { bytes += vocabularyTotalBytes; }
        return bytes;
    }
}
