namespace LocalASR;

/// <summary>
/// Bring a quiet recording up to a level the recognizer works at.
/// </summary>
/// <remarks>
/// <para>
/// Measured, not assumed. Parakeet TDT v3 through sherpa-onnx recognizes a clean Russian
/// utterance perfectly at a −18 dBFS peak, starts to lose words around −50 dBFS and returns
/// nothing at −58 dBFS - and the same utterance attenuated to −68 dBFS (six LSB in a 16-bit
/// file) is recognized perfectly again once it is scaled back up. The features are normalized
/// per mel bin, but the log floor under them is not: a signal that sits at the floor has no
/// pattern left to normalize. Laptop microphone arrays deliver exactly such signals - the first
/// field machine peaked at −25 dBFS with an RMS of −48 dBFS on ordinary speech.
/// </para>
/// <para>
/// So: scale by the peak up to a target, and never down. Not RMS: a pause before the phrase is
/// silence, and an RMS target would amplify it into full-scale noise. The gain is capped so
/// that a take with nothing in it - a muted device, a covered microphone - is not turned into
/// a full-scale hiss and handed to the model as if it were speech, and a take that is silent
/// to the sample is left alone.
/// </para>
/// </remarks>
public static class InputGain
{
    /// <summary>Where the loudest sample ends up; loud enough for the model, clear of clipping.</summary>
    public const float TargetPeak = 0.7f;

    /// <summary>+30 dB. Past this, what is being amplified is the microphone's own noise.</summary>
    public const float MaximumGain = 31.6f;

    /// <summary>Below this the take is digital silence and no gain would make it speech.</summary>
    public const float SilencePeak = 1e-5f;

    /// <summary>The factor <see cref="Normalize"/> would apply; 1 when nothing is to be done.</summary>
    public static float GainFor(ReadOnlySpan<float> samples)
    {
        float peak = Peak(samples);
        if (peak <= SilencePeak || peak >= TargetPeak) { return 1f; }

        return Math.Min(TargetPeak / peak, MaximumGain);
    }

    /// <summary>Scale <paramref name="samples"/> in place; returns the gain applied.</summary>
    public static float Normalize(float[] samples)
    {
        float gain = GainFor(samples);
        if (gain == 1f) { return gain; }

        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] *= gain;
        }

        return gain;
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak) { peak = magnitude; }
        }

        return peak;
    }
}
