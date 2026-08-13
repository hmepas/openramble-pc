using System.Buffers.Binary;

namespace DictationCore.Tests;

public sealed class RecordingRecoveryStoreTests : IDisposable
{
    private readonly string root;
    private readonly string takes;
    private readonly string recovered;

    public RecordingRecoveryStoreTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"recording-recovery-{Guid.NewGuid()}");
        takes = Path.Combine(root, "Takes");
        recovered = Path.Combine(root, "RecoveredAudio");
        Directory.CreateDirectory(takes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Take(string name, int bytes = 8)
    {
        string path = Path.Combine(takes, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, bytes).ToArray());
        Backdate(path);
        return path;
    }

    /// <summary>
    /// A crash leftover is old by the time the app relaunches; a fresh file is
    /// protected by the live-recording guard and must be aged explicitly.
    /// </summary>
    private static void Backdate(string path, double seconds = 120)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-seconds));
    }

    /// <summary>
    /// Header that WAVWriter managed to write before process kill: format
    /// is already valid, but the dimensions are still zero, although the PCM payload is on disk.
    /// </summary>
    private string AbandonedWAV(string name, int sampleBytes = 3200)
    {
        var data = new List<byte>();
        void U16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            data.AddRange(bytes);
        }

        void U32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            data.AddRange(bytes);
        }

        data.AddRange("RIFF"u8);
        U32(36); // process died before the final close
        data.AddRange("WAVEfmt "u8);
        U32(16);
        U16(1);
        U16(1);
        U32(16_000);
        U32(32_000);
        U16(2);
        U16(16);
        data.AddRange("data"u8);
        U32(0); // process died before the final close
        data.AddRange(Enumerable.Repeat((byte)7, sampleBytes));
        string path = Path.Combine(takes, name);
        File.WriteAllBytes(path, data.ToArray());
        Backdate(path);
        return path;
    }

    /// <summary>
    /// A take written moments ago may be the live recording of another
    /// running instance sharing this folder (debug build next to the release
    /// build). The import must leave it exactly where it is - repairing or
    /// deleting it would steal the file out from under the recorder
    /// mid-dictation.
    /// </summary>
    [Fact]
    public async Task TestImportLeavesFreshTakeAloneItMayBeALiveRecording()
    {
        var store = new RecordingRecoveryStore(recovered);
        string live = Path.Combine(takes, "take-live.wav");
        File.WriteAllBytes(live, Enumerable.Repeat((byte)7, 64_000).ToArray());

        var result = await store.ImportAbandoned(takes);

        Assert.Equal(0, result.NewlyImportedCount);
        Assert.Equal(0, result.DiscardedCorruptCount);
        Assert.True(
            File.Exists(live),
            "a possibly-live take must stay in place, byte for byte");
        var kept = await store.Recordings();
        Assert.Empty(kept);
    }

    [Fact]
    public async Task TestPreserveMovesWAVOutOfActiveTakes()
    {
        string source = Take("failed.wav");
        var store = new RecordingRecoveryStore(recovered);

        string? destination = await store.Preserve(source);
        Assert.NotNull(destination);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal(Enumerable.Repeat((byte)7, 8).ToArray(), File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task TestImportAbandonedKeepsCrashRecordingForRetry()
    {
        AbandonedWAV("crash.wav", sampleBytes: 32_000);
        var store = new RecordingRecoveryStore(recovered);

        var result = await store.ImportAbandoned(takes);
        var recordings = result.Recordings;

        Assert.Single(recordings);
        Assert.Equal(0, result.DiscardedCorruptCount);
        Assert.Equal(1, result.NewlyImportedCount);
        Assert.Empty(Directory.GetFileSystemEntries(takes));
        byte[] data = File.ReadAllBytes(recordings[0]);
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40, 4));
        Assert.Equal(36u + 32_000u, riffSize);
        Assert.Equal(32_000u, payloadSize);
    }

    [Fact]
    public async Task TestCorruptFragmentDoesNotBlockValidCrashRecording()
    {
        string corrupt = Take("corrupt.wav", bytes: 10);
        AbandonedWAV("valid.wav", sampleBytes: 32_000);
        var store = new RecordingRecoveryStore(recovered);

        var result = await store.ImportAbandoned(takes);

        Assert.Single(result.Recordings);
        Assert.Equal(1, result.DiscardedCorruptCount);
        Assert.False(File.Exists(corrupt));
        Assert.Empty(Directory.GetFileSystemEntries(takes));
    }

    /// <summary>
    /// A too-short fragment is not "a recording after a failure" - it is an
    /// accidental key press that outlived a kill. The main dictation path
    /// deletes such takes silently; the import must behave the same. Otherwise
    /// the next launch shows "a recording is waiting" whose retry forever
    /// hits an empty result - an error out of thin air.
    /// </summary>
    [Fact]
    public async Task TestImportDeletesTooShortFragmentSilently()
    {
        AbandonedWAV("blip.wav", sampleBytes: 3200); // 0.1 s - below the minimum
        AbandonedWAV("empty.wav", sampleBytes: 0); // header without a single frame
        var store = new RecordingRecoveryStore(recovered);

        var result = await store.ImportAbandoned(takes);

        Assert.Empty(result.Recordings);
        Assert.Equal(0, result.NewlyImportedCount);
        // A fragment is not corruption: no reason to scare with a damage message.
        Assert.Equal(0, result.DiscardedCorruptCount);
        Assert.Empty(Directory.GetFileSystemEntries(takes));
        string[] saved = Directory.Exists(recovered) ? Directory.GetFileSystemEntries(recovered) : [];
        Assert.DoesNotContain(saved, name => name.EndsWith(".wav", StringComparison.Ordinal));
    }

    /// <summary>Exactly at the minimum - already recognizable, keep it.</summary>
    [Fact]
    public async Task TestImportKeepsFragmentAtMinimumDuration()
    {
        int minimumBytes = (int)(DictationDurationPolicy.Minimum.TotalSeconds * 32_000);
        AbandonedWAV("edge.wav", sampleBytes: minimumBytes);
        var store = new RecordingRecoveryStore(recovered);

        var result = await store.ImportAbandoned(takes);

        Assert.Single(result.Recordings);
        Assert.Equal(1, result.NewlyImportedCount);
    }

    /// <summary>
    /// A leftover from last week is not an event of this launch.
    ///
    /// The app used to announce "a recording was found after an interruption"
    /// on every start, even when the failure was a week old and nothing new
    /// happened: the person saw an error where there was none. The count of
    /// new imports lets the app tell "just rescued" from "old leftover".
    /// </summary>
    [Fact]
    public async Task TestLeftoverFromPreviousLaunchIsNotCountedAsNew()
    {
        var store = new RecordingRecoveryStore(recovered);
        await store.Preserve(Take("old.wav", bytes: 64_000));

        var result = await store.ImportAbandoned(takes);

        Assert.Single(result.Recordings);
        Assert.Equal(0, result.NewlyImportedCount);
        Assert.Equal(0, result.DiscardedCorruptCount);
    }

    [Fact]
    public async Task TestLimitsCountAndBytesOldestFirst()
    {
        var store = new RecordingRecoveryStore(
            recovered,
            maximumCount: 2,
            maximumBytes: 12);
        for (int index = 0; index < 3; index++)
        {
            string source = Take($"{index}.wav", bytes: 6);
            await store.Preserve(source);
            await Task.Delay(10);
        }

        var recordings = await store.Recordings();

        Assert.Equal(2, recordings.Count);
        long total = recordings.Sum(path => new FileInfo(path).Length);
        Assert.True(total <= 12);
    }

    [Fact]
    public async Task TestDeletesEntriesOlderThanSevenDays()
    {
        var store = new RecordingRecoveryStore(recovered);
        string old = Take("old.wav");
        await store.Preserve(old);
        var before = await store.Recordings();
        string saved = before[0];
        File.SetLastWriteTimeUtc(saved, DateTime.UtcNow.AddDays(-8));

        await store.Preserve(Take("new.wav"));

        Assert.False(File.Exists(saved));
        var remaining = await store.Recordings();
        Assert.Single(remaining);
    }

    /// <summary>
    /// Launch enforces retention even when nothing new is preserved.
    ///
    /// Preserving prunes too, but a quiet machine may not preserve anything
    /// for weeks - the import pass at launch is what keeps the retention
    /// promise ("kept for a few days") true.
    /// </summary>
    [Fact]
    public async Task TestImportPrunesExpiredRecordingsWithoutNewPreserves()
    {
        var store = new RecordingRecoveryStore(recovered);
        await store.Preserve(Take("old.wav"));
        var before = await store.Recordings();
        string saved = before[0];
        File.SetLastWriteTimeUtc(saved, DateTime.UtcNow.AddDays(-8));

        var result = await store.ImportAbandoned(takes);

        Assert.Equal(0, result.NewlyImportedCount);
        Assert.False(File.Exists(saved));
        var remaining = await store.Recordings();
        Assert.True(remaining.Count == 0, "an expired recording must not outlive launch");
    }
}
