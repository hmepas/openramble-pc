namespace LocalASR;

/// <summary>
/// Which installed model recognizes speech while the app moves to a newer one.
///
/// The app ships one current model and remembers the one it replaced. A machine that already
/// holds the previous model keeps dictating with it after an update; the current model is
/// offered, downloaded only when the person asks, and used from the moment it verifies. Only then
/// is the previous copy removed, so there is never a moment without a working model and never two
/// copies kept for longer than the switch takes.
/// </summary>
/// <remarks>
/// Ported from upstream's <c>ModelLineup.swift</c>. On this port the two models are also two
/// engines - Parakeet Ultra on transcribe.cpp, Parakeet v3 on sherpa-onnx - which changes nothing
/// here: the rule is about files on disk, and <see cref="EngineByModelFiles"/> loads either.
/// </remarks>
public sealed record ModelLineup
{
    public enum Slot
    {
        Current,
        Previous,
        None,
    }

    /// <summary>Why the engine could not load the current model's verified files, if it could not.</summary>
    /// <remarks>
    /// Port addition. Verified files are not yet a working model: the engine may reject them, or
    /// its runtime may not start on this machine at all. Either way the current model does not
    /// serve. Only the first is fixed by fetching the files again.
    /// </remarks>
    public enum LoadFailure
    {
        None,

        /// <summary>The engine rejected the files; a fresh download may fix them.</summary>
        Files,

        /// <summary>The engine's runtime cannot start here; new files would change nothing.</summary>
        Runtime,
    }

    public ModelLineup(ModelState current, ModelState previous, LoadFailure currentLoadFailure = LoadFailure.None)
    {
        bool previousUsable = previous.IsReady;
        // Serving follows "loaded", not "verified": a current model the engine cannot load
        // leaves the previous one serving.
        if (current.IsReady && currentLoadFailure == LoadFailure.None)
        {
            Recognizer = Slot.Current;
            OffersUpgrade = false;
            RemovesPrevious = previous.IsReady || previous.RequiresRepair || previous is ModelState.Failed;
            return;
        }

        Recognizer = previousUsable ? Slot.Previous : Slot.None;
        // An upgrade is only an offer while the old model still works; without one, installing
        // the current model is the ordinary first-run path. Rejected files are offered again; a
        // runtime that cannot start is not, because a download would not change it.
        OffersUpgrade = previousUsable
            && (currentLoadFailure == LoadFailure.Files
                || (currentLoadFailure == LoadFailure.None && CanStart(current)));
        // A damaged old copy is never repaired, only replaced by the current model, so it is
        // cleared as soon as nothing depends on it.
        RemovesPrevious = !previousUsable
            && (previous.RequiresRepair || previous is ModelState.Failed)
            && current is not ModelState.Deleting;
    }

    /// <summary>The model recognition loads, or <see cref="Slot.None"/> when nothing usable is installed.</summary>
    public Slot Recognizer { get; }

    /// <summary>Whether to show the current model as an available upgrade.</summary>
    public bool OffersUpgrade { get; }

    /// <summary>Whether the previous model's files should be deleted now.</summary>
    public bool RemovesPrevious { get; }

    /// <summary>What an install button fetches: always the current model.</summary>
    public Slot Installs => Slot.Current;

    private static bool CanStart(ModelState state) =>
        state is ModelState.NotInstalled or ModelState.Failed or ModelState.RepairRequired;
}
