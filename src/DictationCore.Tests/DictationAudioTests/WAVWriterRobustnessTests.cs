using System.Buffers.Binary;
using System.Text;
using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>
/// The recording is the only copy of what was said.
///
/// The sound comes from the audio stream, and the "stop" and "cancel" commands come from
/// the main one, so recording and completion always go towards each other. Everything
/// checked here is about data loss: silently lost bytes look like a truncated phrase,
/// and they are usually blamed on recognition.
/// </summary>
public sealed class WAVWriterRobustnessTests : IDisposable
{
    private readonly string directory;

    public WAVWriterRobustnessTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"wav-robust-{Guid.NewGuid()}");
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

    private WAVWriter MakeWriter(string name = "take.wav") =>
        new(Path.Combine(directory, name), sampleRate: 16_000, channels: 1);

    // MARK: - Simultaneous recording

    [Fact]
    public async Task TestConcurrentAppendsLoseNothing()
    {
        // Portions of sound come from the audio stream, but there is more than one: changing
        // the device on the go or restarting the engine gives an overlay. A counter of what
        // was written that is not locked drifts away from the file, and the header declares
        // a length that is not there - recognition reads a truncated recording.
        var writer = MakeWriter();
        writer.Open();

        const int Writers = 8;
        const int ChunksEach = 50;
        float[] chunk = [.. Enumerable.Repeat(0.25f, 160)];

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < ChunksEach; index++)
            {
                writer.Append(chunk);
            }
        })));

        const int ExpectedSamples = Writers * ChunksEach * 160;
        Assert.Equal(ExpectedSamples / 16_000.0, writer.Duration.TotalSeconds, 4);

        string path = writer.Close();
        Assert.Equal(
            44 + (ExpectedSamples * 2),
            new FileInfo(path).Length);

        // And the file after the race remains readable by the system, not "almost WAV".
        Assert.Equal(ExpectedSamples, WavFile.Read(path).Length);
    }

    // MARK: - Life cycle

    [Fact]
    public void TestAppendAfterCloseIsRefused()
    {
        // The tail of the sound that came after the stop will not end up in the closed file.
        // To accept it silently means to lose a piece of the phrase without a single trace.
        var writer = MakeWriter();
        writer.Open();
        writer.Append([0.1f, 0.2f]);
        writer.Close();

        Assert.Throws<WAVWriter.Failure.NotOpen>(() => writer.Append([0.3f]));
    }

    [Fact]
    public void TestSecondCloseIsRefused()
    {
        // Stopping can occur twice: when the key is released and when the duration limit
        // is reached. The second close must be a refusal, not a repeated rewriting of the
        // header over a finished file.
        var writer = MakeWriter();
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.1f, 100)]);
        writer.Close();

        Assert.Throws<WAVWriter.Failure.NotOpen>(() => writer.Close());

        // The file remains intact.
        Assert.Equal(100, WavFile.Read(writer.FilePath).Length);
    }

    [Fact]
    public void TestDiscardWithoutOpenDoesNothingBad()
    {
        // Cancel comes from any state, including from before the file managed to appear.
        // You cannot fall on this.
        var writer = MakeWriter();

        writer.Discard();
        writer.Discard();

        Assert.False(File.Exists(writer.FilePath));
    }

    [Fact]
    public void TestReopeningStartsTheRecordingFromScratch()
    {
        // A repeated session must not be appended to the previous recording: otherwise
        // recognition will pick up someone else's phrase spoken a minute ago.
        var writer = MakeWriter();
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.5f, 1000)]);
        writer.Close();

        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.5f, 10)]);
        string path = writer.Close();

        Assert.Equal(10.0 / 16_000, writer.Duration.TotalSeconds, 4);
        Assert.Equal(44 + 20, new FileInfo(path).Length);
    }

    // MARK: - Failure at start

    [Fact]
    public void TestOpenFailsLoudlyWhenTheDirectoryIsBlocked()
    {
        // There is a file in place of the recordings folder - this happens after manual
        // fiddling in the file manager. The dictation must say so, and not start a
        // "recording" that has nowhere to go.
        string blocker = Path.Combine(directory, "blocked");
        File.WriteAllBytes(blocker, Encoding.UTF8.GetBytes("occupied"));
        var writer = new WAVWriter(Path.Combine(blocker, "take.wav"));

        Assert.Throws<WAVWriter.Failure.CannotCreateFile>(() => writer.Open());
    }

    // MARK: - Ran out of space

    [Fact(Skip = "Not ported: the Swift test forces the write failure with POSIX " +
        "setrlimit(RLIMIT_FSIZE) and SIGXFSZ, which has no portable .NET equivalent and " +
        "no counterpart on the target platform. The invariant - a failed append is reported " +
        "instead of silently shortening the phrase, and duration counts only bytes that " +
        "landed - has to be re-pinned on Windows with a real out-of-space seam.")]
    public void TestDiskFullIsReportedInsteadOfSilentlyLosingSound()
    {
        // Deliberately empty: see the skip reason.
    }

    // MARK: - Extreme sizes

    [Fact]
    public void TestEmptyChunkChangesNothing()
    {
        // The audio stream produces an empty buffer during a pause - this is normal.
        var writer = MakeWriter();
        writer.Open();
        writer.Append([]);
        writer.Append([0.1f]);
        writer.Append([]);
        string path = writer.Close();

        Assert.Equal(44 + 2, new FileInfo(path).Length);
    }

    [Fact]
    public void TestLongRecordingKeepsHeaderAndLengthConsistent()
    {
        // A minute of speech is the usual length of a working dictation, and a header with
        // sizes in megabytes must remain true: the system reads the length exactly from
        // there, and not from the actual file.
        var writer = MakeWriter();
        writer.Open();

        float[] second = [.. Enumerable.Range(0, 16_000).Select(index => MathF.Sin(index * 0.01f) * 0.3f)];
        for (int index = 0; index < 60; index++)
        {
            writer.Append(second);
        }

        string path = writer.Close();

        Assert.Equal(60, writer.Duration.TotalSeconds, 3);

        byte[] data = File.ReadAllBytes(path);
        const int ExpectedBytes = 60 * 16_000 * 2;
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40, 4));
        Assert.Equal(36 + ExpectedBytes, (int)riffSize);
        Assert.Equal(ExpectedBytes, (int)dataSize);

        Assert.Equal(60 * 16_000, WavFile.Read(path).Length);
    }
}

/// <summary>File handle after an unsuccessful close.</summary>
public sealed class WAVWriterHandleReleaseTests
{
    [Fact]
    public void TestFailedCloseStillReleasesTheHandle()
    {
        // The file is taken away from under the recording - this happens when the disk is
        // being cleaned.
        string path = Path.Combine(Path.GetTempPath(), $"handle-{Guid.NewGuid()}.wav");
        var writer = new WAVWriter(path, sampleRate: 16_000, channels: 1);
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.1f, 1000)]);
        try
        {
            writer.Close();
        }
        catch (WAVWriter.Failure)
        {
        }

        // The second close must say "not open" rather than try to write into an
        // already closed handle.
        Assert.Throws<WAVWriter.Failure.NotOpen>(() => writer.Close());
        File.Delete(path);
    }

    [Fact]
    public void TestProcessDoesNotRunOutOfFileDescriptors()
    {
        // A thousand dictations in a row: if descriptors leak, the process will run into
        // the limit and stop opening files at all.
        string directory = Path.Combine(Path.GetTempPath(), $"fd-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            for (int index = 0; index < 1000; index++)
            {
                var writer = new WAVWriter(Path.Combine(directory, $"take-{index}.wav"));
                writer.Open();
                writer.Append([0.1f, 0.2f, 0.3f]);
                writer.Close();
            }

            var last = new WAVWriter(Path.Combine(directory, "final.wav"));
            last.Open();
            last.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
