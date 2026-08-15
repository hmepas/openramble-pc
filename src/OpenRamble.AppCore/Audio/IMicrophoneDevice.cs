using DictationCore;

namespace OpenRamble.AppCore.Audio;

/// <summary>
/// A real microphone, as everything that drives one sees it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IAudioCapturing"/> is the dictation kernel's port: start, stop, the take on disk.
/// A test double satisfies it with three fields. This is the rest of what an actual device owes
/// the two things that open one - the shell's overlay and <c>asr-bench mic</c> - and it exists
/// because there is now more than one implementation of it: WASAPI on Windows, PipeWire on Linux.
/// </para>
/// <para>
/// It is deliberately three members and no lifecycle of its own. Everything else a capture does
/// is already promised by <see cref="IAudioCapturing"/>; what a peer implementation must not be
/// free to differ on is the level for the waveform, the way a stream announces its own death, and
/// the counts that tell "delivering nothing" apart from "delivering silence".
/// </para>
/// </remarks>
public interface IMicrophoneDevice : IAudioCapturing, IDisposable
{
    /// <summary>
    /// Raised for every delivery from the device with its peak level in 0…1, for the waveform.
    /// </summary>
    /// <remarks>
    /// A number, never samples. The overlay draws the shape of the signal and must not be handed
    /// anything from which speech could be reconstructed.
    /// </remarks>
    Action<float>? OnInputLevel { get; set; }

    /// <summary>The stream died mid-dictation. Never silent: the session ends and says why.</summary>
    Action<AudioCaptureError>? Failed { get; set; }

    /// <summary>
    /// What the latest session handed to the recorder: frames and samples, counts only.
    /// </summary>
    /// <remarks>
    /// The one number that tells "the microphone is delivering nothing" apart from "the microphone
    /// is delivering silence", and the bench tool's whole answer to "is the device alive". Never
    /// the samples themselves.
    /// </remarks>
    (long Frames, long Samples) DeliveredCounts { get; }
}
