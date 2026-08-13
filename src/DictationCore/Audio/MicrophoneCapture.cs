namespace DictationCore.Audio;

// Partial port of DictationAudio/MicrophoneCapture.swift.
//
// The Swift file is one actor built on AVFoundation: it raises an AVAudioEngine at the
// press of the key, installs a tap on the input node, converts every frame to 16 kHz mono
// with an AVAudioConverter and hands it to the FrameSink. That driver is macOS-only and
// its Windows counterpart (WASAPI) belongs to a later wave, so what is ported here is
// everything the driver does NOT need a microphone for:
//
//   * RecognitionSampleBuffer - the memory cap for the in-process fast path;
//   * the conversion policy (MicrophoneCaptureFormat.Converter) - when a converter is
//     needed at all and what a missing one means;
//   * FailureOnce - the guard that keeps one broken stream from raising dozens of alarms.
//
// Left out, with the reason:
//
//   * the `MicrophoneCapture` actor itself - AVAudioEngine, input node, tap installation,
//     engine start/stop; the capture side of `IAudioCapturing` (StartRecording,
//     StopRecording, AbortRecording, WaitForFirstFrame, StartupLatency,
//     TakeBufferedSamples) is implemented by the Windows capture, not here;
//   * `extractSamples(from:using:target:)` - operates on AVAudioPCMBuffer and
//     AVAudioConverter, i.e. purely platform types. Its portable half - "a conversion
//     failure must be visible, never dropped" - is the contract stated on
//     <see cref="IAudioSampleConverter"/>;
//   * the `os.Logger` - the platform's logging, and the privacy rule that goes with it
//     (never log dictated audio or file names) carries over to whatever replaces it.

/// <summary>
/// A capture format, as much of it as the conversion policy actually reads.
/// </summary>
/// <remarks>
/// Stands in for <c>AVAudioFormat</c>: the Swift policy only ever looks at the sample rate
/// and the channel count, and those two are what any platform can state about its input.
/// </remarks>
public readonly record struct AudioStreamFormat(double SampleRate, int Channels);

/// <summary>
/// Turns frames of the capture device's format into the recording format.
///
/// This is where <c>AVAudioConverter</c> sat: the built-in microphone gives 44.1 or 48 kHz,
/// often stereo, and recognition waits for 16 kHz mono Float32. The platform brings its own
/// resampler; the policy around it is <see cref="MicrophoneCaptureFormat.Converter"/>.
/// </summary>
public interface IAudioSampleConverter
{
    /// <summary>
    /// Convert one captured frame into the target format.
    ///
    /// Input - the source format's samples, output - Float32 mono in the target rate,
    /// in the same order the frames arrived. A conversion that cannot be performed must
    /// throw <see cref="AudioCaptureError.UnsupportedAudioFormat"/>: a frame quietly
    /// dropped here is a word missing from the recording with nothing to show for it.
    /// </summary>
    float[] Convert(float[] samples);
}

/// <summary>Creates the converter for a pair of formats, or <c>null</c> if it cannot.</summary>
public delegate IAudioSampleConverter? AudioSampleConverterFactory(
    AudioStreamFormat source,
    AudioStreamFormat target);

/// <summary>
/// Keeps the common dictation path in memory without turning unlimited recording into
/// unlimited resident memory. Once the cap is crossed, the durable WAV remains complete
/// and recognition falls back to reading it.
/// </summary>
/// <remarks>
/// The Swift original is a mutable <c>struct</c> held as actor state and mutated in place;
/// a small class keeps that meaning without the copy semantics a C# struct would add.
/// </remarks>
internal sealed class RecognitionSampleBuffer
{
    private readonly int maximumSamples;
    private List<float>? samples = [];

    public RecognitionSampleBuffer(int maximumSamples)
    {
        this.maximumSamples = maximumSamples;
    }

    public void Reset()
    {
        samples = [];
    }

    public void Append(float[] newSamples)
    {
        if (samples is null)
        {
            return;
        }

        if (newSamples.Length > maximumSamples - samples.Count)
        {
            samples = null;
            return;
        }

        samples.AddRange(newSamples);
    }

    public float[]? Take()
    {
        float[]? result = samples?.ToArray();
        samples = null;
        return result;
    }

    public void Discard()
    {
        samples = null;
    }
}

/// <summary>
/// The recording format and the decision whether the input has to be converted to it.
/// </summary>
/// <remarks>
/// In Swift these are <c>static</c> members of the <c>MicrophoneCapture</c> actor, described
/// there as a "pure seam to check the critical fork". The actor is not ported, so the seam
/// stands on its own - and keeps the test name <c>MicrophoneCaptureFormatTests</c>.
/// </remarks>
public static class MicrophoneCaptureFormat
{
    /// <summary>
    /// Pure seam to check the critical fork: <c>null</c> is only allowed when no conversion
    /// is really needed.
    /// </summary>
    public static IAudioSampleConverter? Converter(
        AudioStreamFormat source,
        AudioStreamFormat target,
        AudioSampleConverterFactory factory)
    {
        bool needsConversion = source.SampleRate != target.SampleRate
            || source.Channels != target.Channels;
        if (!needsConversion)
        {
            return null;
        }

        return factory(source, target)
            ?? throw new AudioCaptureError.UnsupportedAudioFormat(
                $"couldn't convert {(int)source.SampleRate} Hz / {source.Channels} ch to 16 kHz mono");
    }
}

/// <summary>
/// An audio stream can send several frames before stopping asynchronously.
/// One reason should not create dozens of parallel interrupt tasks.
/// </summary>
internal sealed class FailureOnce
{
    private readonly Lock gate = new();
    private bool didRun;

    public void Run(Action body)
    {
        lock (gate)
        {
            if (didRun)
            {
                return;
            }

            didRun = true;
        }

        body();
    }
}
