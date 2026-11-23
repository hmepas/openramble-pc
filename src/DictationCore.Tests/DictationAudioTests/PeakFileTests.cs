using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

public sealed class PeakFileTests : IDisposable
{
    private readonly string directory;

    public PeakFileTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"peakfile-{Guid.NewGuid()}");
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
    }

    [Fact]
    public void TestBucketsRoundTripPerChannel()
    {
        string path = Path.Combine(directory, "peaks.bin");
        var file = new PeakFile(path, channelCount: 2);
        file.Open();
        file.Append([0.0f, 1.0f]);
        file.Append([0.5f, 0.25f]);
        file.Close();

        PeakFile.Contents contents = PeakFile.Read(path);
        Assert.Equal(PeakFile.DefaultFramesPerBucket, contents.FramesPerBucket);
        Assert.Equal(2, contents.Channels.Length);
        Assert.Equal([0, 128f / 255], contents.Channels[0]);
        Assert.Equal([1, 64f / 255], contents.Channels[1]);
    }

    [Fact]
    public void TestAFileThatNeverClosedStillReadsEveryBucketThatReachedDisk()
    {
        string path = Path.Combine(directory, "peaks.bin");
        var file = new PeakFile(path, channelCount: 2);
        file.Open();
        file.Append([0.2f, 0.4f]);
        file.Append([0.6f, 0.8f]);
        file.Abandon();

        PeakFile.Contents contents = PeakFile.Read(path);
        Assert.Equal(2, contents.Channels[0].Length);
        Assert.Equal(2, contents.Channels[1].Length);
    }

    [Fact]
    public void TestValuesOutsideZeroToOneAreClamped()
    {
        string path = Path.Combine(directory, "peaks.bin");
        var file = new PeakFile(path, channelCount: 2);
        file.Open();
        file.Append([-3, 7]);
        file.Close();
        Assert.Equal([0f, 1f], PeakFile.Read(path).Channels.Select(channel => channel[0]));
    }

    [Fact]
    public void TestTheWrongNumberOfPeaksIsRefused()
    {
        var file = new PeakFile(Path.Combine(directory, "peaks.bin"), channelCount: 2);
        file.Open();
        Assert.Throws<PeakFile.Failure.WriteFailed>(() => file.Append([0.5f]));
        file.Close();
    }

    [Fact]
    public void TestAForeignFileIsRejectedRatherThanMisread()
    {
        string path = Path.Combine(directory, "not-peaks.bin");
        File.WriteAllText(path, "hello world, this is not a peak file");
        Assert.Throws<PeakFile.Failure.Malformed>(() => PeakFile.Read(path));
    }
}
