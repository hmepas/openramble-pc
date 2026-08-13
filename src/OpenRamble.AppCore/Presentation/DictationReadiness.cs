using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// Why pressing a key now won't start anything.
///
/// The kernel rejects the start silently - and does the right thing: it has neither a screen nor
/// words. But for a person, silence is indistinguishable from a breakdown. This is most painful
/// in the warm-up window immediately after installation: they just did everything right, press a
/// key, and nothing happens - no sound, no panel, no explanation.
///
/// A separate type, not branches inside <c>AppState</c>: the set of reasons is checked with a
/// table, and not by pressing a live key on a live microphone.
/// </summary>
/// <remarks>
/// The macOS original also gates on the Accessibility grant. Windows has no such permission -
/// a keyboard hook and synthetic input need no consent - so the microphone is the only
/// permission that can hold a press back, and the "two permissions" branch is gone with it.
/// </remarks>
public static class DictationReadiness
{
    /// <summary>
    /// <c>null</c> - dictation can start.
    ///
    /// The order is the same as the onboarding order: first the permission, then the model, then
    /// the warm-up. A person is told the first thing that holds them, not all three at once - a
    /// list of three items in a panel that lives four seconds does not read.
    /// </summary>
    public static string? Reason(
        bool microphoneGranted,
        ModelState modelState,
        bool isEngineReady,
        bool engineWasReadyBefore = false)
    {
        if (!microphoneGranted)
        {
            return "Dictation needs microphone access. Open Settings → General → Permissions.";
        }

        switch (modelState)
        {
            case ModelState.NotInstalled:
                return "The recognition model isn't downloaded yet. Open Settings → Model.";
            case ModelState.Downloading:
                return "The recognition model is still downloading.";
            case ModelState.Verifying:
                return "The downloaded model is still being verified.";
            case ModelState.Deleting:
                return "The recognition model is being deleted.";
            case ModelState.RepairRequired:
            case ModelState.Failed:
                return "The recognition model needs repair. Open Settings → Model.";
            case ModelState.Ready:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(modelState));
        }

        if (!isEngineReady && !engineWasReadyBefore)
        {
            // The most offensive case: everything is installed, everything is granted, and still
            // silence. We name the deadline - otherwise it is unclear whether to wait a second or
            // restart the application.
            //
            // Only the FIRST warm-up blocks. Once the engine has ever been ready, an unloaded
            // engine (residency gave its memory back) must not stop the press: recording starts
            // instantly and the reload rides under the voice.
            return "The model is getting ready for this PC — usually 20–40 seconds, and only once.";
        }

        return null;
    }
}
