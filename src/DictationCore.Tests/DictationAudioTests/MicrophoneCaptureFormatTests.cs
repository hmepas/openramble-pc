using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

public sealed class MicrophoneCaptureFormatTests
{
    private static AudioStreamFormat Format(double rate, int channels) => new(rate, channels);

    [Fact]
    public void TestMatchingFormatNeedsNoConverter()
    {
        var target = Format(rate: 16_000, channels: 1);

        var converter = MicrophoneCaptureFormat.Converter(
            source: target,
            target: target,
            factory: (_, _) =>
            {
                Assert.Fail("the factory must not be called");
                return null;
            });

        Assert.Null(converter);
    }

    [Fact]
    public void TestFailedConverterFor48kStereoIsFatal()
    {
        var source = Format(rate: 48_000, channels: 2);
        var target = Format(rate: 16_000, channels: 1);

        Assert.Throws<AudioCaptureError.UnsupportedAudioFormat>(
            () => MicrophoneCaptureFormat.Converter(source, target, factory: (_, _) => null));
    }

    [Fact(Skip = "Not ported: the Swift test asserts that AVAudioConverter really supports " +
        "48 kHz stereo -> 16 kHz mono, i.e. a platform capability rather than our policy. " +
        "The equivalent belongs to the Windows capture wave, where a real resampler exists " +
        "behind IAudioSampleConverter.")]
    public void Test48kStereoGetsARealConverter()
    {
        // Deliberately empty: see the skip reason.
    }

    [Fact(Skip = "Not ported: MicrophoneCapture.extractSamples works on AVAudioPCMBuffer and " +
        "AVAudioConverter, which is exactly the macOS-only part left out of this port. The " +
        "invariant - a conversion failure at runtime surfaces as unsupportedAudioFormat " +
        "instead of being dropped - moves to the Windows capture wave.")]
    public void TestRuntimeConversionFailureIsNotSilentlyDropped()
    {
        // Deliberately empty: see the skip reason.
    }

    [Fact]
    public void TestRecognitionBufferMovesSamplesWithoutKeepingASecondCopy()
    {
        var buffer = new RecognitionSampleBuffer(maximumSamples: 4);
        buffer.Append([0.1f, 0.2f]);
        buffer.Append([0.3f, 0.4f]);

        float[]? taken = buffer.Take();
        Assert.NotNull(taken);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], taken);
        Assert.Null(buffer.Take());
    }

    /// <summary>
    /// With an owner that ends the take at the cap, the complete bounded prefix is what
    /// recognition reads - and the frame that did not fit is refused, so the WAV can be closed
    /// at the same point and the two describe the same recording.
    /// </summary>
    [Fact]
    public void TestRecognitionBufferKeepsItsPrefixAtTheCapWhenAskedTo()
    {
        var buffer = new RecognitionSampleBuffer(maximumSamples: 3, keepsPrefixAtLimit: true);
        Assert.True(buffer.Append([0.1f, 0.2f]));
        Assert.False(buffer.Append([0.3f, 0.4f]));
        // Once crossed, nothing more is kept: a late frame must not slip in behind the limit.
        Assert.False(buffer.Append([0.5f]));

        float[]? kept = buffer.Take();
        Assert.NotNull(kept);
        Assert.Equal([0.1f, 0.2f], kept);
    }

    [Fact]
    public void TestRecognitionBufferFallsBackToWAVAfterItsMemoryCap()
    {
        var buffer = new RecognitionSampleBuffer(maximumSamples: 3);
        buffer.Append([0.1f, 0.2f]);
        buffer.Append([0.3f, 0.4f]);
        buffer.Append([0.5f]);

        // Overflow must disable the in-memory path for the whole take.
        Assert.Null(buffer.Take());

        // A new take gets a fresh fast-path buffer.
        buffer.Reset();
        buffer.Append([0.6f]);
        float[]? fresh = buffer.Take();
        Assert.NotNull(fresh);
        Assert.Equal([0.6f], fresh);
    }
}
