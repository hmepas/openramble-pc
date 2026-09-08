using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// The level rule in front of the recognizer. Numbers, not a screen: a −25 dBFS laptop take
/// must come up, a hot take must not be touched, and silence must stay silence.
/// </summary>
public sealed class InputGainTests
{
    private static float[] Sine(float peak, int samples = 1600)
    {
        var buffer = new float[samples];
        for (int index = 0; index < samples; index++)
        {
            buffer[index] = peak * MathF.Sin(index * 0.05f);
        }

        return buffer;
    }

    /// <summary>The first field laptop: peak 0.059 (−25 dBFS). It has to reach the target.</summary>
    [Fact]
    public void AQuietTakeIsBroughtUpToTheTargetPeak()
    {
        float[] samples = Sine(0.059f);

        float gain = InputGain.Normalize(samples);

        Assert.InRange(gain, 11.5f, 12.0f);
        Assert.InRange(InputGain.Peak(samples), InputGain.TargetPeak - 0.01f, InputGain.TargetPeak + 0.01f);
    }

    [Fact]
    public void ALoudTakeIsLeftAlone()
    {
        float[] samples = Sine(0.9f);
        float[] before = [.. samples];

        Assert.Equal(1f, InputGain.Normalize(samples));
        Assert.Equal(before, samples);
    }

    /// <summary>−68 dBFS: six LSB in a 16-bit file. The cap is what stops it at +30 dB.</summary>
    [Fact]
    public void TheGainIsCappedSoNoiseIsNotPromotedToSpeech()
    {
        float[] samples = Sine(0.0004f);

        Assert.Equal(InputGain.MaximumGain, InputGain.Normalize(samples));
    }

    [Fact]
    public void DigitalSilenceStaysSilent()
    {
        var samples = new float[1600];
        samples[10] = 1e-6f;

        Assert.Equal(1f, InputGain.Normalize(samples));
        Assert.Equal(1e-6f, samples[10]);
    }

    /// <summary>The rule is applied on the way into the engine, not somewhere a caller can skip.</summary>
    [Fact]
    public async Task TheTranscriberNormalizesBeforeTheEngineSeesTheSamples()
    {
        var engine = new StubEngine();
        var transcriber = new LocalTranscriber(engine);
        string directory = Path.Combine(Path.GetTempPath(), $"gain-{Guid.NewGuid():D}");
        Directory.CreateDirectory(directory);
        try
        {
            await transcriber.Prepare(directory);
            _ = await transcriber.Transcribe(Sine(0.059f));

            float[] received = engine.ReceivedBatches[^1];
            Assert.InRange(InputGain.Peak(received), InputGain.TargetPeak - 0.01f, InputGain.TargetPeak + 0.01f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
