using System.Globalization;

namespace OpenRamble.AppCore;

/// <summary>
/// When to tell a person their microphone is too quiet, and what to say.
/// </summary>
/// <remarks>
/// <para>
/// The number comes from measurement, not taste. The recognizer (Parakeet TDT v3, weight-only
/// quantized) starts to lose words on a clean utterance around a −50 dBFS peak and returns
/// nothing at −58 dBFS; the gain rule in front of it (<c>LocalASR.InputGain</c>) moves that
/// cliff away, but what it amplifies past a point is the microphone's own noise, and the words
/// come back garbled instead of missing. −35 dBFS is a comfortable margin above the cliff and
/// still far below where any healthy microphone sits during speech (−6…−25 dBFS). The first
/// field laptop peaked at −25 dBFS and is meant to pass without a hint.
/// </para>
/// <para>
/// Said once per run. It is advice about the hardware, and hardware does not change between
/// dictations; a hint on every take would be nagging.
/// </para>
/// </remarks>
public static class MicrophoneLevelHint
{
    /// <summary>−35 dBFS as a linear peak.</summary>
    public const float QuietPeak = 0.0178f;

    public static bool IsTooQuiet(float peak) => peak > 0f && peak < QuietPeak;

    /// <summary>The take's peak in dBFS, whole; <c>-inf</c> spelled out for digital silence.</summary>
    public static string Decibels(float peak) =>
        peak <= 0f
            ? "-inf"
            : Math.Round(20 * Math.Log10(peak)).ToString(CultureInfo.InvariantCulture);

    public static string Message(float peak) =>
        $"The microphone is very quiet ({Decibels(peak)} dB). Move closer, or raise its"
        + " input volume in Windows Sound settings.";
}
