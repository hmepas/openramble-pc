using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Moves a recording's directory to the desktop's Trash, where the person can still get it back.
/// </summary>
/// <remarks>
/// A port rather than a call so a test can remove rather than fill the developer's Trash, and so
/// a desktop without a Trash can say so: when <see cref="IsAvailable"/> is false the caller asks
/// before deleting for good (<see cref="MeetingStore.DeletePermanently"/>) instead of pretending
/// the recording can be restored.
/// </remarks>
public interface IRecordingTrasher
{
    bool IsAvailable { get; }

    /// <exception cref="IOException">The item could not be moved to the Trash.</exception>
    void Trash(string path);
}

/// <summary>
/// The freedesktop Trash through <c>gio trash</c>, resolved on <c>PATH</c> the safe way
/// (<see cref="ExecutablePath"/>). Without <c>gio</c> it reports itself unavailable.
/// </summary>
public sealed class GioTrasher : IRecordingTrasher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string? gio;

    public GioTrasher(string? searchPath = null)
    {
        gio = ExecutablePath.Find("gio", searchPath);
    }

    public bool IsAvailable => gio is not null;

    public void Trash(string path)
    {
        if (gio is null) { throw new IOException("gio is not installed, so there is no Trash to move to"); }

        var start = new ProcessStartInfo(gio)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("trash");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);

        using Process process = Process.Start(start) ?? throw new IOException("gio did not start");
        Task<string> error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new IOException("gio trash did not finish");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"gio trash failed ({process.ExitCode}): {error.Result.Trim()}");
        }
    }
}

/// <summary>
/// The recordings on disk: one directory per recording, named by its id.
/// </summary>
/// <remarks>
/// <code>
/// Recordings/
///   &lt;uuid&gt;/            published
///     audio.wav        stereo 16 kHz; L = microphone, R = system audio
///     audio.m4a        compact AAC archive after transcription completes
///     meta.json
///     transcript.json
///     peaks.bin
///   .incomplete/&lt;uuid&gt;/  in progress, or abandoned by a crash
/// </code>
/// A recording is written under <c>.incomplete/</c> and moved into place when it ends - a rename
/// within a volume, O(1) at any size - so the list never contains a half-written directory.
/// Whatever a crash leaves behind is repaired and published at the next launch.
/// <para>
/// Not <see cref="DictationHistoryStore"/>, on purpose: that is a bounded list of short takes; a
/// recording is unbounded and has a transcript that grows while it records.
/// </para>
/// </remarks>
public sealed class MeetingStore
{
    public const string IncompleteDirectoryName = ".incomplete";
    public const string MetadataFileName = "meta.json";
    public const string TranscriptFileName = "transcript.json";
    public const string VideoFileName = "video.mp4";
    public const string CompressedAudioFileName = "audio.m4a";

    // SEAM(wave4a-B): upstream reads these three from MeetingWriter (audioFileName, peaksFileName,
    // sampleRate). Point them there once MeetingWriter lands.
    public const string AudioFileName = "audio.wav";
    public const string PeaksFileName = "peaks.bin";
    private const int SampleRate = 16_000;

    private readonly string root;
    private readonly IRecordingTrasher trasher;

    public MeetingStore(string root, IRecordingTrasher? trasher = null)
    {
        this.root = root;
        this.trasher = trasher ?? new GioTrasher();
    }

    // Paths

    public string IncompleteRoot => Path.Combine(root, IncompleteDirectoryName);

    public string Directory(Guid id) => Path.Combine(root, id.ToString());

    public string IncompleteDirectory(Guid id) => Path.Combine(IncompleteRoot, id.ToString());

    public string? AudioPath(Guid id) => RawAudioPath(id) ?? CompressedAudioPath(id);

    /// <summary>
    /// The PCM source is kept while transcription is live and until the compact archive has been
    /// written. Older recordings always have this.
    /// </summary>
    public string? RawAudioPath(Guid id) => Existing(Path.Combine(Directory(id), AudioFileName));

    public string? CompressedAudioPath(Guid id) => Existing(Path.Combine(Directory(id), CompressedAudioFileName));

    /// <summary>The optional movie beside the WAV. Audio-only recordings have no movie file.</summary>
    public string? VideoPath(Guid id) => Existing(Path.Combine(Directory(id), VideoFileName));

    public string? PeaksPath(Guid id) => Existing(Path.Combine(Directory(id), PeaksFileName));

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    // Reading

    /// <summary>
    /// Every published recording, newest first. Only directories whose name parses as an id
    /// count, which also skips <c>.incomplete</c> and stray files without a filter for either.
    /// </summary>
    public IReadOnlyList<MeetingRecordingMetadata> List()
    {
        string[] entries;
        try
        {
            entries = System.IO.Directory.GetDirectories(root);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return entries
            .Select(entry => Guid.TryParse(Path.GetFileName(entry), out Guid id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Select(Metadata)
            .OfType<MeetingRecordingMetadata>()
            .OrderByDescending(metadata => metadata.StartedAt)
            .ToList();
    }

    /// <summary>Whether the recording has ended and moved into the list.</summary>
    public bool IsPublished(Guid id) => System.IO.Directory.Exists(Directory(id));

    public MeetingRecordingMetadata? Metadata(Guid id) =>
        Read<MeetingRecordingMetadata>(Path.Combine(Directory(id), MetadataFileName));

    public MeetingTranscript? Transcript(Guid id) =>
        Read<MeetingTranscript>(Path.Combine(Directory(id), TranscriptFileName));

    private static T? Read<T>(string path)
        where T : class
    {
        try
        {
            return MeetingRecordingCoding.Decode<T>(File.ReadAllBytes(path));
        }
        // A damaged file is not worth failing the launch over, and not worth deleting either -
        // the audio beside it is intact and still plays.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>What one recording occupies.</summary>
    public long Bytes(Guid id)
    {
        try
        {
            return new DirectoryInfo(Directory(id)).GetFiles().Sum(file => file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Writing

    public void Write(MeetingRecordingMetadata metadata, bool incomplete = false)
    {
        string directory = incomplete ? IncompleteDirectory(metadata.Id) : Directory(metadata.Id);
        System.IO.Directory.CreateDirectory(directory);
        WriteAtomically(Path.Combine(directory, MetadataFileName), MeetingRecordingCoding.Encode(metadata));
    }

    public void Write(MeetingTranscript transcript, Guid id, bool incomplete = false)
    {
        string directory = incomplete ? IncompleteDirectory(id) : Directory(id);
        WriteAtomically(Path.Combine(directory, TranscriptFileName), MeetingRecordingCoding.Encode(transcript));
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        string staging = path + ".new";
        File.WriteAllBytes(staging, data);
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>Move a finished recording into the list.</summary>
    public void Publish(Guid id)
    {
        System.IO.Directory.CreateDirectory(root);
        System.IO.Directory.Move(IncompleteDirectory(id), Directory(id));
    }

    /// <summary>Retitle a recording; a blank title means no title.</summary>
    public MeetingRecordingMetadata? Rename(Guid id, string? title)
    {
        MeetingRecordingMetadata? metadata = Metadata(id);
        if (metadata is null) { return null; }

        string? trimmed = title?.Trim();
        metadata.Title = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        Write(metadata);
        return metadata;
    }

    /// <summary>
    /// Whether <see cref="Trash"/> can work here. When it cannot, the UI confirms and calls
    /// <see cref="DeletePermanently"/> instead.
    /// </summary>
    public bool CanTrash => trasher.IsAvailable;

    /// <summary>Hand the whole recording directory to the Trash.</summary>
    /// <exception cref="IOException">There is no Trash (<see cref="CanTrash"/>), or the move failed.</exception>
    public void Trash(Guid id) => trasher.Trash(Directory(id));

    /// <summary>Remove the recording for good - only after the person agreed to.</summary>
    public void DeletePermanently(Guid id) => System.IO.Directory.Delete(Directory(id), recursive: true);

    // Recovery

    /// <summary>
    /// Repair and publish whatever a crash left under <c>.incomplete/</c>.
    /// </summary>
    /// <remarks>
    /// The WAV header of an unfinished recording still says zero bytes; the audio after it is
    /// intact. A directory whose file is still held by a live process - a second instance - is
    /// left alone.
    /// </remarks>
    public IReadOnlyList<MeetingRecordingMetadata> RecoverIncomplete(DateTimeOffset? now = null)
    {
        string[] entries;
        try
        {
            entries = System.IO.Directory.GetDirectories(IncompleteRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var recovered = new List<MeetingRecordingMetadata>();
        foreach (string entry in entries)
        {
            if (!Guid.TryParse(Path.GetFileName(entry), out Guid id)) { continue; }

            string audio = Path.Combine(entry, AudioFileName);
            if (!File.Exists(audio))
            {
                // Nothing recorded at all: a directory with no audio is not a recording anyone
                // can want back.
                try { System.IO.Directory.Delete(entry, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                continue;
            }

            if (IsActivelyHeld(audio)) { continue; }

            int frames;
            try
            {
                frames = RepairHeader(audio);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // A crash can leave the movie with its temporary suffix; publish it beside the
            // repaired audio so the recording stays a screen take.
            string incompleteVideo = Path.Combine(entry, VideoFileName + ".incomplete");
            string video = Path.Combine(entry, VideoFileName);
            if (File.Exists(incompleteVideo) && !File.Exists(video))
            {
                try { File.Move(incompleteVideo, video); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            MeetingRecordingMetadata metadata =
                Read<MeetingRecordingMetadata>(Path.Combine(entry, MetadataFileName))
                ?? MeetingRecordingMetadata.Create(
                    id: id,
                    startedAt: CreationTime(audio) ?? now ?? DateTimeOffset.Now,
                    systemAudio: new SystemAudioSummary(wasRequested: false));
            metadata.Duration = (double)frames / SampleRate;
            metadata.EndReason = MeetingEndReason.CrashRecovered;
            if (metadata.TranscriptionState == MeetingTranscriptionState.Live)
            {
                metadata.TranscriptionState = MeetingTranscriptionState.Partial;
            }

            try
            {
                Write(metadata, incomplete: true);
                Publish(id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            recovered.Add(metadata);
        }

        return recovered;
    }

    private static DateTimeOffset? CreationTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetCreationTimeUtc(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether another open handle owns the file. Upstream asks <c>flock</c>; .NET takes an
    /// exclusive <c>flock</c> on Unix (a share-mode lock on Windows) when a file is opened with
    /// <see cref="FileShare.None"/>, so the probe asks the same question. A path that cannot be
    /// opened for any other reason counts as held: repairing bytes whose ownership cannot be
    /// proved is the worse mistake.
    /// </summary>
    /// <remarks>
    /// SEAM(wave4a-B): the writer must keep <c>audio.wav</c> open with <see cref="FileShare.None"/>
    /// for the whole recording, or a live recording is not seen here.
    /// </remarks>
    private static bool IsActivelyHeld(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private const int HeaderBytes = 44;
    private const int BytesPerFrame = 4;

    /// <summary>
    /// Rewrite the RIFF and data sizes from the file's actual length; returns the frame count the
    /// file now declares.
    /// </summary>
    /// <remarks>
    /// SEAM(wave4a-B): a local copy of upstream <c>MeetingWriter.repairHeader(at:)</c> (stereo
    /// 16-bit, 44-byte header). Replace with <c>MeetingWriter.RepairHeader</c> once it lands.
    /// </remarks>
    /// <exception cref="IOException">The file is not a WAV this app wrote, or cannot be updated.</exception>
    internal static int RepairHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> head = stackalloc byte[HeaderBytes];
        if (stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false) != HeaderBytes
            || !head[0..4].SequenceEqual("RIFF"u8)
            || !head[8..12].SequenceEqual("WAVE"u8)
            || !head[36..40].SequenceEqual("data"u8))
        {
            throw new IOException("not a WAV this app wrote");
        }

        long dataBytes = Math.Max(0, stream.Length - HeaderBytes) / BytesPerFrame * BytesPerFrame;
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(36 + dataBytes));
        stream.Position = 4;
        stream.Write(size);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)dataBytes);
        stream.Position = 40;
        stream.Write(size);
        stream.Flush(flushToDisk: true);
        return (int)(dataBytes / BytesPerFrame);
    }
}
