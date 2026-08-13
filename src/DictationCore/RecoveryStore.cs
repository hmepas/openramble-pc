using System.Buffers.Binary;
using System.Globalization;

namespace DictationCore;

// `RecoveryStore` used to live here, writing unrecognized text to disk.
// Now such text is kept only in process memory (Copy/Retry in the menu),
// and only WAVs end up on the disk after a technical error - see below.
// The `Recovered/` directory of old assemblies is removed by the application at startup.

/// <summary>WAV remaining after a technical error and available for re-ASR.</summary>
public interface IRecordingRecoveryStoring
{
    /// <summary>The path of the kept copy, or <c>null</c> if nothing was kept.</summary>
    Task<string?> Preserve(string source);
}

public sealed record AbandonedRecordingImportResult
{
    public AbandonedRecordingImportResult(
        IReadOnlyList<string> recordings,
        int newlyImportedCount,
        int discardedCorruptCount)
    {
        Recordings = recordings;
        NewlyImportedCount = newlyImportedCount;
        DiscardedCorruptCount = discardedCorruptCount;
    }

    public IReadOnlyList<string> Recordings { get; }

    /// <summary>
    /// How many recordings this very import rescued.
    ///
    /// Tells "something happened right now" apart from "left over from last
    /// week": an old leftover is not an event of this launch, and announcing
    /// it again would be a made-up error.
    /// </summary>
    public int NewlyImportedCount { get; }

    public int DiscardedCorruptCount { get; }

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(AbandonedRecordingImportResult? other) =>
        other is not null
        && Recordings.SequenceEqual(other.Recordings)
        && NewlyImportedCount == other.NewlyImportedCount
        && DiscardedCorruptCount == other.DiscardedCorruptCount;

    public override int GetHashCode() =>
        HashCode.Combine(Recordings.Count, NewlyImportedCount, DiscardedCorruptCount);
}

/// <remarks>
/// The Swift original throws <c>CocoaError</c> codes; here the cases are nested
/// exception types so that the catch stays exactly as narrow as the original's.
/// </remarks>
public abstract class RecordingRecoveryError : Exception
{
    private protected RecordingRecoveryError(string message)
        : base(message)
    {
    }

    /// <summary>Swift's <c>CocoaError(.fileReadCorruptFile)</c>: the WAV is not ours or is cut short.</summary>
    public sealed class CorruptFile : RecordingRecoveryError
    {
        public CorruptFile()
            : base("the recording is corrupt")
        {
        }
    }

    /// <summary>Swift's <c>CocoaError(.fileNoSuchFile)</c>: the path does not belong to the store.</summary>
    public sealed class NoSuchFile : RecordingRecoveryError
    {
        public NoSuchFile()
            : base("no such file in the recovery directory")
        {
        }
    }
}

/// <summary>Production recovery for erroneous and interrupted recordings.</summary>
/// <remarks>
/// The Swift original is an <c>actor</c>: the state is one directory on disk and
/// every operation on it is serialized. Here a lock does the serializing - the
/// work itself is synchronous file IO, so there is nothing to await inside.
/// </remarks>
public sealed class RecordingRecoveryStore : IRecordingRecoveryStoring
{
    private readonly Lock gate = new();
    private readonly string directory;
    private readonly int maximumCount;
    private readonly TimeSpan maximumAge;
    private readonly long maximumBytes;

    public RecordingRecoveryStore(
        string directory,
        int maximumCount = 10,
        TimeSpan? maximumAge = null,
        long maximumBytes = 1_073_741_824)
    {
        this.directory = directory;
        this.maximumCount = maximumCount;
        this.maximumAge = maximumAge ?? TimeSpan.FromDays(7);
        this.maximumBytes = maximumBytes;
    }

    public Task<string?> Preserve(string source)
    {
        lock (gate)
        {
            return Task.FromResult(PreserveHere(source));
        }
    }

    /// <summary>Transfer WAV remaining to Takes after kill/crash/power loss.</summary>
    public Task<AbandonedRecordingImportResult> ImportAbandoned(string takesDirectory)
    {
        lock (gate)
        {
            return Task.FromResult(ImportAbandonedHere(takesDirectory));
        }
    }

    public Task<IReadOnlyList<string>> Recordings()
    {
        lock (gate)
        {
            return Task.FromResult(RecordingsHere());
        }
    }

    public Task Delete(string path)
    {
        lock (gate)
        {
            string prefix = Standardized(directory) + Path.DirectorySeparatorChar;
            if (!Standardized(path).StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new RecordingRecoveryError.NoSuchFile();
            }

            File.Delete(path);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Byte rate of our own WAVWriter stream: 16 kHz x 16-bit x mono.
    /// The format is pinned by the header check in <see cref="RepairAbandonedWAV"/>.
    /// </summary>
    private const long BytesPerSecond = 32_000;

    /// <summary>
    /// How old a take must be before the import may treat it as abandoned.
    /// Longer than any realistic gap between writes of a live recording,
    /// far shorter than the time to relaunch after a real crash.
    /// </summary>
    internal static readonly TimeSpan AbandonedMinimumAge = TimeSpan.FromSeconds(60);

    private string? PreserveHere(string source)
    {
        Directory.CreateDirectory(directory);
        string destination;
        if (SameDirectory(Path.GetDirectoryName(Path.GetFullPath(source)), directory))
        {
            destination = source;
        }
        else
        {
            destination = Path.Combine(directory, NewRecordingName());
            File.Move(source, destination);
        }

        Prune();
        return File.Exists(destination) ? destination : null;
    }

    /// <summary>
    /// <c>recording-&lt;ISO 8601 instant&gt;-&lt;8 hex digits&gt;.wav</c>, as in the original.
    ///
    /// The colons of the timestamp are replaced, exactly as the Swift did: there
    /// they only looked nicer that way, here a colon may not appear in a Windows
    /// file name at all.
    /// </summary>
    private static string NewRecordingName()
    {
        string timestamp = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            .Replace(":", "-", StringComparison.Ordinal);
        string unique = Guid.NewGuid().ToString("D")[..8];
        return $"recording-{timestamp}-{unique}.wav";
    }

    private AbandonedRecordingImportResult ImportAbandonedHere(string takesDirectory)
    {
        if (!Directory.Exists(takesDirectory))
        {
            if (Directory.Exists(directory))
            {
                Prune();
            }

            return new AbandonedRecordingImportResult(
                recordings: RecordingsHere(),
                newlyImportedCount: 0,
                discardedCorruptCount: 0);
        }

        int newlyImportedCount = 0;
        int discardedCorruptCount = 0;
        foreach (string entry in Directory.GetFiles(takesDirectory))
        {
            if (!IsWAV(entry))
            {
                continue;
            }

            // A take modified moments ago is not abandoned - it may be the
            // live recording of ANOTHER running instance (a debug build and
            // the release build share this folder). Touching it would steal
            // the file out from under the recorder mid-dictation. A genuine
            // crash leftover ages past this window and is picked up on the
            // next launch.
            DateTimeOffset modified = File.GetLastWriteTimeUtc(entry);
            if (DateTimeOffset.UtcNow - modified < AbandonedMinimumAge)
            {
                continue;
            }

            try
            {
                long payloadBytes = RepairAbandonedWAV(entry);
                // A fragment shorter than the recognition minimum is an
                // accidental key press that outlived a kill, not lost speech.
                // The main dictation path deletes such takes silently; the
                // import must behave the same - otherwise every launch shows
                // "a recording after a failure" whose retry can only ever
                // produce an empty result.
                if (!DictationDurationPolicy.IsWorthTranscribing(
                        TimeSpan.FromSeconds((double)payloadBytes / BytesPerSecond)))
                {
                    File.Delete(entry);
                    continue;
                }

                if (PreserveHere(entry) is not null)
                {
                    newlyImportedCount++;
                }
            }
            catch (RecordingRecoveryError.CorruptFile)
            {
                // An exactly unusable fragment should not block the rest of the
                // recordings after a crash. This is only our own WAVWriter file
                // from Takes; other people's files do not get here. The deletion
                // reaches the UI counter, so it is not hidden from humans.
                File.Delete(entry);
                discardedCorruptCount++;
            }
        }

        // Launch is the retention checkpoint. Preserving prunes too, but a
        // quiet machine may not preserve anything for weeks - without this
        // pass an old recording would outlive the promised retention window.
        if (Directory.Exists(directory))
        {
            Prune();
        }

        return new AbandonedRecordingImportResult(
            recordings: RecordingsHere(),
            newlyImportedCount: newlyImportedCount,
            discardedCorruptCount: discardedCorruptCount);
    }

    /// <summary>
    /// WAVWriter first puts a 44-byte header with zero dimensions and fixes them
    /// in <c>close()</c>. After kill/crash the PCM is already on the disk, but
    /// without this fix the system decoder considers the recording empty. We
    /// accept only the exact format of our own writer: someone else's or a
    /// cropped file must not masquerade as a suitable Retry.
    ///
    /// Returns the PCM payload size: it decides whether the recording holds
    /// anything worth transcribing.
    /// </summary>
    private static long RepairAbandonedWAV(string path)
    {
        long totalBytes = new FileInfo(path).Length;
        if (totalBytes < 44)
        {
            throw new RecordingRecoveryError.CorruptFile();
        }

        long payloadBytes = totalBytes - 44;
        if (payloadBytes % 2 != 0 || payloadBytes > uint.MaxValue - 36)
        {
            throw new RecordingRecoveryError.CorruptFile();
        }

        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        byte[] header = new byte[44];
        handle.Seek(0, SeekOrigin.Begin);
        handle.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20, 2)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)) != 16_000
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4)) != 32_000
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2)) != 2
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2)) != 16
            || !header.AsSpan(36, 4).SequenceEqual("data"u8))
        {
            throw new RecordingRecoveryError.CorruptFile();
        }

        byte[] value = new byte[4];
        handle.Seek(4, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(36 + payloadBytes));
        handle.Write(value);
        handle.Seek(40, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)payloadBytes);
        handle.Write(value);
        handle.Flush(flushToDisk: true);
        return payloadBytes;
    }

    private IReadOnlyList<string> RecordingsHere()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Entries()
            .OrderByDescending(entry => entry.Date)
            .Select(entry => entry.Path)
            .ToArray();
    }

    private readonly record struct Entry(string Path, DateTimeOffset Date, long Bytes);

    private List<Entry> Entries()
    {
        var entries = new List<Entry>();
        foreach (string path in Directory.GetFiles(directory))
        {
            if (!IsWAV(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            entries.Add(new Entry(path, info.LastWriteTimeUtc, info.Length));
        }

        return entries;
    }

    private void Prune()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var survivors = new List<Entry>();
        foreach (var entry in Entries().OrderBy(entry => entry.Date))
        {
            if (now - entry.Date > maximumAge)
            {
                File.Delete(entry.Path);
            }
            else
            {
                survivors.Add(entry);
            }
        }

        long totalBytes = survivors.Sum(entry => entry.Bytes);
        while (survivors.Count > maximumCount || totalBytes > maximumBytes)
        {
            var oldest = survivors[0];
            survivors.RemoveAt(0);
            File.Delete(oldest.Path);
            totalBytes -= oldest.Bytes;
        }
    }

    private static bool IsWAV(string path) =>
        Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Swift compares <c>standardizedFileURL</c>s: <c>.</c> and <c>..</c> resolved,
    /// symlinks left alone, the characters compared as they are.
    /// </summary>
    private static bool SameDirectory(string? one, string another) =>
        one is not null
        && string.Equals(Standardized(one), Standardized(another), StringComparison.Ordinal);

    private static string Standardized(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>Old unit tests and pure consumers can explicitly choose to remove WAV.</summary>
public sealed class DiscardingRecordingRecovery : IRecordingRecoveryStoring
{
    public Task<string?> Preserve(string source)
    {
        File.Delete(source);
        return Task.FromResult<string?>(null);
    }
}
