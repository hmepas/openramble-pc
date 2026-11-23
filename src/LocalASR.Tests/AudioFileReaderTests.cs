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

    // MARK: - Bounded reads

    [Fact]
    public void BoundedStereoReadsPreserveTheResampledTail()
    {
        string path = WavFixture.WriteFloat32(directory, seconds: 4.013, sampleRate: 48_000, channels: 2);
        float[] whole = reader.Samples(path);
        var pieces = new List<float>();
        using (AudioFileStream stream = AudioFileStream.Open(path))
        {
            while (true)
            {
                float[] piece = stream.Read(997);
                if (piece.Length == 0) { break; }
                Assert.True(piece.Length <= 997);
                pieces.AddRange(piece);
            }
        }

        Assert.Equal(whole.Length, pieces.Count);
        Assert.Equal(4.013, pieces.Count / 16_000.0, 0.002);
        Assert.True(pieces.TakeLast(100).Max(Math.Abs) > 0.1f, "the resampler's final samples are kept");
        Assert.True(pieces.Zip(whole, (a, b) => Math.Abs(a - b)).Max() < 0.0001f);
    }

    // MARK: - Channels

    [Fact]
    public void RightOnlyStereoSpeechSurvivesMonoConversion()
    {
        float[] right = Enumerable.Range(0, 48_000).Select(index => MathF.Sin(index * 0.03f) * 0.4f).ToArray();
        string path = WavFixture.WriteChannels(directory, 48_000, [new float[48_000], right]);

        float[] samples = reader.Samples(path);

        Assert.Equal(16_000, samples.Length);
        // Meeting files can carry the other speaker only on the right.
        Assert.True(samples.Max(Math.Abs) > 0.1f, "the other speaker must not be discarded");
    }

    // MARK: - Compressed files (ffmpeg)

    [FfmpegFact]
    public void CompressedStereoDecodesThroughFfmpegWithBothChannels()
    {
        float[] right = Enumerable.Range(0, 48_000 * 3).Select(index => MathF.Sin(index * 0.03f) * 0.4f).ToArray();
        string wav = WavFixture.WriteChannels(directory, 48_000, [new float[right.Length], right]);
        string compressed = Path.Combine(directory, "right only.m4a");
        FfmpegFactAttribute.Convert(wav, compressed);

        var samples = new List<float>();
        using (AudioFileStream stream = AudioFileStream.Open(compressed))
        {
            while (stream.Read() is { Length: > 0 } piece) { samples.AddRange(piece); }
        }

        // AAC adds priming and padding; the length is the same within a few frames.
        Assert.Equal(3.0, samples.Count / 16_000.0, 0.1);
        Assert.True(samples.Max(Math.Abs) > 0.1f, "the other speaker must not be discarded");
    }

    [Fact]
    public void AFileThatIsNotAudioIsReportedNotGuessed()
    {
        // Not RIFF, so it goes to ffmpeg - which finds nothing, or is not installed. Either way
        // an honest error, never silence.
        string path = Path.Combine(directory, "notes.m4a");
        File.WriteAllText(path, "not audio");

        Assert.Throws<AudioFileReader.Failure.Unreadable>(() =>
        {
            using AudioFileStream stream = AudioFileStream.Open(path);
            while (stream.Read().Length > 0) { }
        });
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
