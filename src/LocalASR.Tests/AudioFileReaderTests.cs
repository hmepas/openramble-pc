using System.Text;

namespace LocalASR.Tests;

/// <summary>
/// The audio reader is the bottleneck between recording and recognition.
///
/// Recognition is capable of exactly one format: mono, 16 kHz, Float32. Users' microphones
/// give anything: a headset gives 8 kHz, the built-in input 44.1, an external interface 48,
/// and stereo comes from any USB camera. A conversion mistake does not look like an error:
/// the speech is simply recognized as slurred, or comes out twice as long as it was.
/// </summary>
public class AudioFileReaderTests : IDisposable
{
    private readonly string directory;
    private readonly AudioFileReader reader = new();

    public AudioFileReaderTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():D}");
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static void AssertRoughly(int actual, int expected, int tolerance, string message) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{message}: got {actual}, expected ~{expected}");

    // MARK: - Frequencies

    [Fact]
    public void ReadsOwnRecordingWithoutTouchingIt()
    {
        // Our own dictation recording is already in the target format: no conversion is
        // needed here, and the number of samples must match exactly.
        string path = WavFixture.WriteFloat32(directory, seconds: 0.5, sampleRate: 16_000, channels: 1);

        float[] samples = reader.Samples(path);

        Assert.Equal(8000, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
    }

    [Fact]
    public void UpsamplesNarrowbandHeadset()
    {
        // A Bluetooth headset gives 8 kHz in call mode. Without the conversion recognition
        // would get a recording twice as short.
        string path = WavFixture.WriteFloat32(directory, seconds: 1.0, sampleRate: 8_000, channels: 1);

        float[] samples = reader.Samples(path);

        AssertRoughly(samples.Length, 16_000, 400, "8 kHz must become 16 kHz");
    }

    [Fact]
    public void DownsamplesBuiltInMicrophone()
    {
        // A Mac's built-in input is 44.1 kHz.
        string path = WavFixture.WriteFloat32(directory, seconds: 1.0, sampleRate: 44_100, channels: 1);

        float[] samples = reader.Samples(path);

        AssertRoughly(samples.Length, 16_000, 400, "44.1 kHz must become 16 kHz");
    }

    [Fact]
    public void DownsamplesExternalInterface()
    {
        // An external audio interface gives 48 kHz.
        string path = WavFixture.WriteFloat32(directory, seconds: 1.0, sampleRate: 48_000, channels: 1);

        float[] samples = reader.Samples(path);

        AssertRoughly(samples.Length, 16_000, 400, "48 kHz must become 16 kHz");
    }

    // MARK: - Channels

    [Fact]
    public void MixesStereoDownToMono()
    {
        // USB cameras and most interfaces give two channels. The engine expects one: handed
        // the stereo as it is, the channels would be glued end to end and speech would turn
        // into an accelerated mess.
        string path = WavFixture.WriteFloat32(directory, seconds: 1.0, sampleRate: 44_100, channels: 2);

        float[] samples = reader.Samples(path);

        AssertRoughly(samples.Length, 16_000, 400, "stereo must fold down to mono");
        Assert.Contains(samples, sample => sample != 0);
    }

    // MARK: - Long recordings

    [Fact]
    public void DurationLimitRejectsBeforeBuildingFullSampleArray()
    {
        string path = WavFixture.WriteFloat32(directory, seconds: 2, sampleRate: 48_000, channels: 2);

        var failure = Assert.Throws<AudioFileReader.Failure.DurationExceeded>(
            () => reader.Samples(path, maximumDuration: TimeSpan.FromSeconds(1)));
        Assert.Equal(2, failure.Actual.TotalSeconds, precision: 2);
        Assert.Equal(TimeSpan.FromSeconds(1), failure.Maximum);
    }

    [Fact]
    public void ReadsRecordingLongerThanOneConversionChunk()
    {
        // The conversion happens in pieces of 16 384 frames, and gluing the pieces together
        // is the most fragile place: a mistake there cuts off the end of a long dictation.
        string path = WavFixture.WriteFloat32(directory, seconds: 3.0, sampleRate: 48_000, channels: 1);

        float[] samples = reader.Samples(path);

        AssertRoughly(samples.Length, 48_000, 800, "three seconds must not lose their tail");
    }

    // MARK: - Broken inputs

    [Fact]
    public void BrokenHeaderIsReportedNotGuessed()
    {
        // A file with a .wav extension but no sound: this is what a broken recording looks
        // like after the application crashes. Silently returning an empty array is not
        // allowed - it would read as "the dictation heard silence" instead of an honest
        // error.
        string path = Path.Combine(directory, "broken.wav");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("RIFF это не звук, а обрывок"));

        Assert.Throws<AudioFileReader.Failure.Unreadable>(() => reader.Samples(path));
    }

    [Fact]
    public void MissingFileIsReported()
    {
        string path = Path.Combine(directory, "нет-такого.wav");

        Assert.Throws<AudioFileReader.Failure.Unreadable>(() => reader.Samples(path));
    }

    [Fact]
    public void EmptyRecordingIsReportedSeparately()
    {
        // Pressed and immediately released: the file is correct, but empty. That is a
        // separate case, not a format failure - and they must be told apart so as not to
        // scare the person with a message about broken sound.
        string path = WavFixture.WriteFloat32(
            directory, seconds: 0, sampleRate: 16_000, channels: 1, name: "empty");

        Assert.Throws<AudioFileReader.Failure.EmptyFile>(() => reader.Samples(path));
    }
}
