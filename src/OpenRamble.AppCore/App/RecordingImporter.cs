using System.ComponentModel;
using System.Diagnostics;
using DictationCore;
using DictationCore.Audio;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>The speech engine as the importer uses it: one piece of audio in, its words out.</summary>
public interface IImportRecognizer
{
    Task<string> Transcribe(float[] samples, CancellationToken cancellationToken);
}

/// <summary>
/// The app's own <see cref="LocalTranscriber"/> - the one dictation uses. The importer never
/// loads or unloads it; it only asks for a piece at a time while the model is ready.
/// </summary>
public sealed class SharedTranscriberRecognizer(LocalTranscriber transcriber) : IImportRecognizer
{
    public async Task<string> Transcribe(float[] samples, CancellationToken cancellationToken) =>
        (await transcriber.Transcribe(samples, cancellationToken).ConfigureAwait(false)).Text;
}

/// <summary>Makes the compact archive of a recording whose transcript is whole.</summary>
public interface IRecordingAudioCompressor
{
    /// <summary>
    /// Write an AAC copy of <paramref name="source"/> at <paramref name="destination"/> and check
    /// that it decodes to the same length. False when it cannot; the source is never touched.
    /// </summary>
    Task<bool> Compress(string source, string destination, CancellationToken cancellationToken);
}

/// <summary>
/// AAC in an M4A container through <c>ffmpeg</c>, resolved on <c>PATH</c> the safe way
/// (<see cref="ExecutablePath"/>). Without <c>ffmpeg</c> nothing is compressed and the WAV stays.
/// </summary>
/// <remarks>
/// Upstream compacts with <c>AVAudioFile</c> at 96 kbit/s, keeping both channels; so does this.
/// The archive is checked by decoding it back: a length within half a second of the source's is
/// a file that plays.
/// </remarks>
public sealed class FfmpegAudioCompressor : IRecordingAudioCompressor
{
    public const string BitRate = "96k";

    private static readonly double Tolerance = 0.5 * MeetingWriter.SampleRate;

    private readonly string? ffmpeg;

    public FfmpegAudioCompressor(string? searchPath = null)
    {
        ffmpeg = ExecutablePath.Find("ffmpeg", searchPath);
    }

    public bool IsAvailable => ffmpeg is not null;

    public async Task<bool> Compress(string source, string destination, CancellationToken cancellationToken)
    {
        if (ffmpeg is null) { return false; }

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
                 {
                     "-nostdin", "-v", "error", "-threads", "1", "-y",
                     "-protocol_whitelist", "file",
                     "-i", "file:" + Path.GetFullPath(source),
                     "-vn", "-c:a", "aac", "-b:a", BitRate, "-f", "ipod",
                     "file:" + Path.GetFullPath(destination),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start) ?? throw new Win32Exception("ffmpeg did not start");
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) { return false; }
        }
        catch (Win32Exception)
        {
            return false;
        }

        long expected = MeetingWriter.FrameCountAt(source);
        long decoded = await Task.Factory.StartNew(
            () => DecodedFrames(destination, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
        return decoded > 0 && Math.Abs(decoded - expected) <= Tolerance;
    }

    private static long DecodedFrames(string path, CancellationToken cancellationToken)
    {
        try
        {
            using AudioFileStream stream = AudioFileStream.Open(path);
            long total = 0;
            while (stream.Read(MeetingWriter.SampleRate * 10) is { Length: > 0 } block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += block.Length;
            }

            return total;
        }
        catch (Exception error) when (error is AudioFileReader.Failure or IOException)
        {
            return 0;
        }
    }
}

/// <summary>
/// Audio and video files brought into the Library, and their transcripts.
/// </summary>
/// <remarks>
/// <para>
/// An imported file becomes a recording like any other: its audio is copied into the recordings
/// store in the store's own format (mono on the microphone channel, silence on the other), so it
/// plays, shows a waveform and is deleted exactly as a voice note is. The original is only read.
/// </para>
/// <para>
/// <b>Deviation from upstream - one engine.</b> Upstream reads files with a second recognizer of
/// its own. Here the file shares the dictation engine (<see cref="Edges.Recognizer"/>): it is
/// never loaded or unloaded from here, a job runs only while the model is ready, and before every
/// piece (at most twenty seconds of audio) it waits its turn under
/// <see cref="DictationPriority"/> (<see cref="Edges.WaitForTurn"/>), so a dictation always goes
/// first and waits at most for the piece in flight. The owner should keep the engine resident
/// while <see cref="Activity"/> is not idle; if the model goes away mid-file the file goes back to
/// the head of the queue and starts over when <see cref="ModelsChanged"/> says it is back.
/// </para>
/// <para>
/// <b>Deviation from upstream - compression here.</b> Once the transcript is whole the WAV is
/// compacted to <c>audio.m4a</c> (<see cref="Edges.Compressor"/>); the WAV is deleted only after
/// the archive verified, and stays when there is no compressor.
/// </para>
/// <para>
/// <b>A quit loses nothing.</b> A recording marks itself as an import (state live) until its
/// transcript is whole, and its audio is already in the store, so the next launch takes it up
/// again from the start (<see cref="Resume"/>).
/// </para>
/// <para>
/// Thread-safe. Every callback is raised on a background thread; the owner marshals.
/// </para>
/// </remarks>
public sealed class RecordingImporter
{
    /// <summary>Why a file could not be brought in.</summary>
    public enum Failure
    {
        /// <summary>Not audio or video that can be decoded here.</summary>
        Unreadable,

        /// <summary>It decodes, and there is no sound in it at all.</summary>
        Empty,

        /// <summary>The Library could not keep it: the disk, most likely.</summary>
        CannotStore,
    }

    /// <summary>A file that could not be brought in, and why.</summary>
    public sealed class ImportFailedException(Failure failure) : Exception($"import failed: {failure}")
    {
        public Failure Failure { get; } = failure;
    }

    public sealed record Edges(
        MeetingStore Store,
        IImportRecognizer Recognizer,
        Func<bool> IsModelReady,
        Func<ITextProcessing> MakePipeline)
    {
        /// <summary>Returns once nobody is dictating; production passes <see cref="DictationPriority.WaitForTurn"/>.</summary>
        public Func<CancellationToken, Task> WaitForTurn { get; init; } = static _ => Task.CompletedTask;

        public Func<DateTimeOffset> Now { get; init; } = static () => DateTimeOffset.Now;

        /// <summary>Null keeps the WAV.</summary>
        public IRecordingAudioCompressor? Compressor { get; init; }
    }

    /// <summary>
    /// Three pieces in a row that will not decode are the engine, not the audio: stop and say the
    /// transcript is not whole.
    /// </summary>
    public const int FailuresBeforeStop = 3;

    public const string CopyPrefix = "import-";

    private const string ArchiveStagingName = MeetingStore.CompressedAudioFileName + ".incomplete";

    private readonly Lock gate = new();
    private readonly Edges edges;
    private readonly List<Guid> queue = [];
    private readonly HashSet<Guid> cancelled = [];
    private readonly CancellationTokenSource stopping = new();
    private RecordingImportActivity activity = new();
    private int copying;
    private Guid? transcribing;
    private double fraction;
    private Task copyChain = Task.CompletedTask;
    private bool workerRunning;

    public RecordingImporter(Edges edges)
    {
        this.edges = edges;
    }

    public RecordingImportActivity Activity
    {
        get { lock (gate) { return activity; } }
    }

    public Action<RecordingImportActivity> OnActivity { get; set; } = static _ => { };

    /// <summary>A file is in the Library, with its audio and no transcript yet.</summary>
    public Action<MeetingRecordingMetadata> OnImported { get; set; } = static _ => { };

    /// <summary>The transcript so far, after every piece.</summary>
    public Action<Guid, IReadOnlyList<MeetingUtterance>> OnTranscript { get; set; } = static (_, _) => { };

    /// <summary>Transcription ended and the recording says how on disk.</summary>
    public Action<Guid> OnFinished { get; set; } = static _ => { };

    /// <summary>A file could not be brought in. The name (without extension) is for the notice only.</summary>
    public Action<Failure, string> OnFailure { get; set; } = static (_, _) => { };

    private bool IsStopping => stopping.IsCancellationRequested;

    // Bringing files in

    /// <summary>Copy these files into the Library, one after another, and queue their transcripts.</summary>
    /// <returns>The copying, for a caller (or a test) that wants to await it.</returns>
    public Task Add(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || IsStopping) { return Task.CompletedTask; }

        Task chain;
        Change(() =>
        {
            copying += files.Count;
            Task previous = copyChain;
            copyChain = Task.Factory.StartNew(
                () =>
                {
                    previous.Wait();
                    foreach (string file in files) { BringIn(file); }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        });
        lock (gate) { chain = copyChain; }
        return chain;
    }

    private void BringIn(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        try
        {
            MeetingRecordingMetadata metadata = Store(file, edges.Store, edges.Now(), stopping.Token);
            OnImported(metadata);
            Enqueue(metadata.Id);
        }
        catch (OperationCanceledException)
        {
            // A quit: the half copy is already gone, and nobody is left to tell.
        }
        catch (ImportFailedException failure)
        {
            OnFailure(failure.Failure, name);
        }
        catch (Exception)
        {
            OnFailure(Failure.CannotStore, name);
        }
        finally
        {
            Change(() => copying--);
        }
    }

    /// <summary>
    /// Take up the transcripts an earlier run did not finish, oldest first, whose WAV is there:
    /// imported recordings still live or waiting for the model (from the start), and live
    /// recordings a quit or a crash stopped before their transcript caught up - live, waiting,
    /// or partial with audio not yet decoded (from where the transcript ends, per channel). Call
    /// after <see cref="MeetingStore.RecoverIncomplete"/>; half copies are removed here first.
    /// </summary>
    public void Resume(IEnumerable<MeetingRecordingMetadata> recordings)
    {
        RemoveHalfCopies(edges.Store);
        IEnumerable<MeetingRecordingMetadata> unfinished = recordings
            // The copy the pieces are read from. Without it there is nothing to transcribe.
            .Where(recording => edges.Store.RawAudioPath(recording.Id) is { } audio
                && (recording.IsImported
                    ? recording.TranscriptionState is MeetingTranscriptionState.Live or MeetingTranscriptionState.WaitingForModel
                    : recording.TranscriptionState is MeetingTranscriptionState.Live or MeetingTranscriptionState.WaitingForModel or MeetingTranscriptionState.Partial
                        && HasUndecodedAudio(recording, audio)))
            .OrderBy(recording => recording.StartedAt);
        foreach (MeetingRecordingMetadata recording in unfinished) { Enqueue(recording.Id); }
        ResumeCompaction(recordings);
    }

    /// <summary>A channel of a live recording whose transcript stops short of its audio.</summary>
    private bool HasUndecodedAudio(MeetingRecordingMetadata recording, string audio)
    {
        long frames = MeetingWriter.FrameCountAt(audio);
        Dictionary<MeetingChannel, int> decoded = edges.Store.Transcript(recording.Id)?.DecodedFrames ?? [];
        return Channels(recording).Any(channel => decoded.GetValueOrDefault(channel) < frames);
    }

    /// <summary>The channels with something to transcribe: the other side only if it ever sounded.</summary>
    private static IReadOnlyList<MeetingChannel> Channels(MeetingRecordingMetadata? recording) =>
        recording is { IsImported: false, SystemAudio.EverDeliveredAudio: true }
            ? [MeetingChannel.Microphone, MeetingChannel.System]
            : [MeetingChannel.Microphone];

    /// <summary>
    /// A quit during <see cref="Compact"/> (an import's or a live recording's) leaves a whole
    /// transcript beside its WAV, and maybe a half-written archive: drop the half archive and
    /// compact again, one after another.
    /// </summary>
    private void ResumeCompaction(IEnumerable<MeetingRecordingMetadata> recordings)
    {
        var pending = new List<Guid>();
        foreach (MeetingRecordingMetadata recording in recordings)
        {
            try { File.Delete(Path.Combine(edges.Store.Directory(recording.Id), ArchiveStagingName)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

            if (recording.TranscriptionState == MeetingTranscriptionState.Complete
                && edges.Store.RawAudioPath(recording.Id) is not null)
            {
                pending.Add(recording.Id);
            }
        }

        if (pending.Count == 0
            || edges.Compressor is null
            || edges.Compressor is FfmpegAudioCompressor { IsAvailable: false })
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (Guid id in pending)
            {
                if (IsStopping) { return; }
                await Compact(id).ConfigureAwait(false);
                // The audio path changed: the owner reads the recording again.
                if (!Abandoned(id) && edges.Store.CompressedAudioPath(id) is not null) { OnFinished(id); }
            }
        });
    }

    /// <summary>The model became ready, or stopped being: look at the queue again.</summary>
    public void ModelsChanged()
    {
        Change(static () => { });
        StartWorkerIfNeeded();
    }

    /// <summary>The recording is going away (to the Trash). Its transcript is no longer wanted.</summary>
    public void Cancel(Guid id) =>
        Change(() =>
        {
            if (!queue.Remove(id) && transcribing == id) { cancelled.Add(id); }
        });

    /// <summary>
    /// The app is ending. Nothing more is written, so what is unfinished stays marked as
    /// unfinished and is taken up at the next launch.
    /// </summary>
    public void Stop() => stopping.Cancel();

    // The queue

    private void Enqueue(Guid id)
    {
        Change(() =>
        {
            if (!queue.Contains(id) && transcribing != id) { queue.Add(id); }
        });
        StartWorkerIfNeeded();
    }

    /// <summary>Mutate under the lock, then publish the activity if it changed.</summary>
    private void Change(Action mutate)
    {
        RecordingImportActivity? changed = null;
        lock (gate)
        {
            mutate();
            var next = new RecordingImportActivity
            {
                Copying = copying,
                Transcribing = transcribing,
                Fraction = fraction,
                Waiting = [.. queue],
                NeedsModel = queue.Count > 0 && !edges.IsModelReady(),
            };
            if (!Same(next, activity))
            {
                activity = next;
                changed = next;
            }
        }

        if (changed is not null) { OnActivity(changed); }
    }

    private static bool Same(RecordingImportActivity a, RecordingImportActivity b) =>
        a.Copying == b.Copying
        && a.Transcribing == b.Transcribing
        && a.Fraction.Equals(b.Fraction)
        && a.NeedsModel == b.NeedsModel
        && a.Waiting.SequenceEqual(b.Waiting);

    private void StartWorkerIfNeeded()
    {
        lock (gate)
        {
            if (workerRunning || IsStopping || queue.Count == 0 || !edges.IsModelReady()) { return; }
            workerRunning = true;
        }

        _ = Task.Run(Work);
    }

    private async Task Work()
    {
        while (true)
        {
            Guid? next = null;
            Change(() =>
            {
                if (IsStopping || queue.Count == 0 || !edges.IsModelReady())
                {
                    workerRunning = false;
                    return;
                }

                next = queue[0];
                queue.RemoveAt(0);
                transcribing = next;
                fraction = 0;
            });
            if (next is not { } id) { return; }

            try
            {
                await Transcribe(id).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (IsStopping)
            {
            }
            catch (Exception)
            {
                // The dictation lock or the disk refused: say so on the recording.
                await Finish(id, MeetingTranscriptionState.Failed).ConfigureAwait(false);
            }
            finally
            {
                Change(() =>
                {
                    cancelled.Remove(id);
                    transcribing = null;
                    fraction = 0;
                });
            }
        }
    }

    // One transcript

    private bool Abandoned(Guid id)
    {
        lock (gate) { return IsStopping || cancelled.Contains(id); }
    }

    private async Task Transcribe(Guid id)
    {
        MeetingStore store = edges.Store;
        CancellationToken stop = stopping.Token;
        if (store.RawAudioPath(id) is not { } audio)
        {
            await Finish(id, MeetingTranscriptionState.Failed).ConfigureAwait(false);
            return;
        }

        // An import starts over; a live recording goes on from where its transcript stops.
        MeetingRecordingMetadata? metadata = store.Metadata(id);
        MeetingTranscript? earlier = metadata is { IsImported: false } ? store.Transcript(id) : null;
        var decodedFrames = new Dictionary<MeetingChannel, int>(earlier?.DecodedFrames ?? []);
        IReadOnlyList<MeetingChannel> channels = Channels(metadata);
        List<(MeetingChannel Channel, PieceRange Range)> pieces;
        int frames;
        try
        {
            (pieces, frames) = await Task.Factory.StartNew(
                () =>
                {
                    int length = (int)Math.Min(int.MaxValue, MeetingWriter.FrameCountAt(audio));
                    List<(MeetingChannel, PieceRange)> planned = [.. channels
                        .SelectMany(channel => Plan(audio, channel, decodedFrames.GetValueOrDefault(channel), stop)
                            .Select(range => (channel, range)))
                        // In the order they were spoken, as the live queue would have had them.
                        .OrderBy(piece => piece.range.Start)];
                    return (planned, length);
                },
                stop,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Finish(id, MeetingTranscriptionState.Failed).ConfigureAwait(false);
            return;
        }

        ITextProcessing pipeline = edges.MakePipeline();
        var reader = new MeetingPcmWindowReader(audio);
        double rate = MeetingWriter.SampleRate;
        long total = Math.Max(1, pieces.Sum(piece => (long)piece.Range.Length));
        var assembler = new MeetingUtteranceAssembler();
        var closed = new List<MeetingUtterance>(earlier?.Utterances ?? []);
        long done = 0;
        int failuresInARow = 0;

        foreach ((MeetingChannel channel, PieceRange piece) in pieces)
        {
            await edges.WaitForTurn(stop).ConfigureAwait(false);
            if (Abandoned(id)) { return; }
            if (!edges.IsModelReady())
            {
                Requeue(id);
                return;
            }

            var start = TimeSpan.FromSeconds(piece.Start / rate);
            var end = TimeSpan.FromSeconds(piece.End / rate);
            MeetingUtteranceAssembler.Segment decoded;
            try
            {
                float[] samples = reader.Read(new MeetingSegmentRef(channel, piece.Start, piece.Length));
                string text = await edges.Recognizer.Transcribe(samples, stop).ConfigureAwait(false);
                decoded = new(channel, start, end, pipeline.Run(text).Output.Text);
                failuresInARow = 0;
            }
            catch (Exception) when (!IsStopping)
            {
                // The model was unloaded under the decode: not this piece's failure.
                if (!edges.IsModelReady())
                {
                    Requeue(id);
                    return;
                }

                decoded = new(channel, start, end, string.Empty, IsFailed: true);
                failuresInARow++;
            }

            // The recording was moved to the Trash, or the app is quitting, while it ran.
            if (Abandoned(id)) { return; }
            closed.AddRange(assembler.Append(decoded));
            done += piece.Length;
            List<MeetingUtterance> soFar = [.. closed];
            if (assembler.Pending is { } pending) { soFar.Add(pending); }
            decodedFrames[channel] = Math.Max(decodedFrames.GetValueOrDefault(channel), piece.End);
            TryWrite(new MeetingTranscript(soFar, decodedFrames), id);
            OnTranscript(id, soFar);
            Change(() => fraction = (double)done / total);
            if (failuresInARow >= FailuresBeforeStop)
            {
                await Finish(id, MeetingTranscriptionState.Partial).ConfigureAwait(false);
                return;
            }
        }

        if (Abandoned(id)) { return; }
        closed.AddRange(assembler.Flush());
        // Every channel was read to its end; the silence after the last piece is decoded too.
        foreach (MeetingChannel channel in channels) { decodedFrames[channel] = frames; }
        TryWrite(new MeetingTranscript(closed, decodedFrames), id);
        OnTranscript(id, closed);
        await Finish(id, MeetingTranscriptionState.Complete).ConfigureAwait(false);
    }

    /// <summary>The model went away mid-file: first in line again, from the start, once it is back.</summary>
    private void Requeue(Guid id) =>
        Change(() =>
        {
            // Trashed (or the app is quitting) while the model went away: not wanted again.
            if (IsStopping || cancelled.Contains(id)) { return; }
            if (!queue.Contains(id)) { queue.Insert(0, id); }
        });

    private void TryWrite(MeetingTranscript transcript, Guid id)
    {
        try
        {
            edges.Store.Write(transcript, id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The audio is still there, and the next piece writes the whole transcript again.
        }
    }

    private async Task Finish(Guid id, MeetingTranscriptionState state)
    {
        if (Abandoned(id)) { return; }
        try
        {
            // One step under the store's lock: a rename meanwhile is kept, and a recording
            // trashed meanwhile is not written back.
            edges.Store.Update(id, metadata => metadata.TranscriptionState = state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        // Gone (to the Trash) under the transcript: nobody is left to tell.
        if (!edges.Store.IsPublished(id)) { return; }
        if (state == MeetingTranscriptionState.Complete) { await Compact(id).ConfigureAwait(false); }
        if (Abandoned(id)) { return; }
        OnFinished(id);
    }

    /// <summary>
    /// The transcript is whole, so the PCM source has done its job: an AAC archive replaces it.
    /// The WAV goes only after the archive verified and moved into place; the peaks stay.
    /// </summary>
    /// <remarks>Also the archive of a finished live recording, which the app hands here.</remarks>
    public async Task Compact(Guid id)
    {
        if (edges.Compressor is not { } compressor || edges.Store.RawAudioPath(id) is not { } source) { return; }

        string directory = edges.Store.Directory(id);
        string staging = Path.Combine(directory, ArchiveStagingName);
        string archive = Path.Combine(directory, MeetingStore.CompressedAudioFileName);
        try
        {
            File.Delete(staging);
            if (!await compressor.Compress(source, staging, stopping.Token).ConfigureAwait(false)) { return; }
            if (Abandoned(id)) { return; }
            File.Move(staging, archive, overwrite: true);
            File.Delete(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }
        finally
        {
            try { File.Delete(staging); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Off the worker

    /// <summary>
    /// Copy a file's sound into the store as a published recording: mono at 16 kHz on the
    /// microphone channel, silence on the other - the shape of a voice note.
    /// </summary>
    /// <exception cref="ImportFailedException">Unreadable, empty, or not storable; nothing is left on disk.</exception>
    public static MeetingRecordingMetadata Store(
        string file,
        MeetingStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        AudioFileStream stream;
        try
        {
            stream = AudioFileStream.Open(file);
        }
        catch (AudioFileReader.Failure.EmptyFile)
        {
            throw new ImportFailedException(Failure.Empty);
        }
        catch (AudioFileReader.Failure)
        {
            throw new ImportFailedException(Failure.Unreadable);
        }

        using (stream)
        {
            var id = Guid.NewGuid();
            // Copied under a name the store's crash recovery does not read. A quit in the middle
            // of a copy would otherwise come back at the next launch as an untitled recording of
            // half the file. The file itself is untouched, so a half copy is simply dropped.
            string directory = CopyDirectory(id, store);
            MeetingWriter? writer = null;
            bool finished = false;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                writer = new MeetingWriter(directory);
                writer.Open();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    float[] block;
                    try
                    {
                        block = stream.Read(MeetingWriter.SampleRate);
                    }
                    catch (AudioFileReader.Failure.EmptyFile)
                    {
                        throw new ImportFailedException(Failure.Empty);
                    }
                    catch (Exception error) when (error is AudioFileReader.Failure or IOException)
                    {
                        throw new ImportFailedException(Failure.Unreadable);
                    }

                    if (block.Length == 0) { break; }
                    writer.Append(block, new float[block.Length]);
                }

                writer.Finish();
                finished = true;
                if (writer.FrameCount == 0) { throw new ImportFailedException(Failure.Empty); }

                string name = Path.GetFileNameWithoutExtension(file).Trim();
                MeetingRecordingMetadata metadata = MeetingRecordingMetadata.Create(
                    startedAt: now,
                    systemAudio: new SystemAudioSummary(wasRequested: false),
                    id: id,
                    duration: writer.Duration.TotalSeconds,
                    title: name.Length == 0 ? null : name,
                    endReason: MeetingEndReason.StoppedByUser,
                    transcriptionState: MeetingTranscriptionState.Live,
                    importLanguage: RecordingImportLanguage.Automatic);
                // Whole now: it takes the store's own path into the list.
                System.IO.Directory.Move(directory, store.IncompleteDirectory(id));
                store.Write(metadata, incomplete: true);
                store.Publish(id);
                return metadata;
            }
            catch (Exception error)
            {
                if (!finished) { writer?.Abandon(); }
                Remove(directory);
                Remove(store.IncompleteDirectory(id));
                if (error is ImportFailedException or OperationCanceledException) { throw; }
                throw new ImportFailedException(Failure.CannotStore);
            }
        }
    }

    private static void Remove(string directory)
    {
        try { System.IO.Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static string CopyDirectory(Guid id, MeetingStore store) =>
        Path.Combine(store.IncompleteRoot, CopyPrefix + id);

    /// <summary>Copies a quit cut short. Nothing reads them, so they only take space.</summary>
    public static void RemoveHalfCopies(MeetingStore store)
    {
        string[] entries;
        try
        {
            entries = System.IO.Directory.GetDirectories(store.IncompleteRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string entry in entries.Where(entry => Path.GetFileName(entry).StartsWith(CopyPrefix, StringComparison.Ordinal)))
        {
            Remove(entry);
        }
    }

    /// <summary>Where the stored audio is cut for the engine: 4 s windows into the planner.</summary>
    public static IReadOnlyList<PieceRange> Plan(string audio, CancellationToken cancellationToken = default) =>
        Plan(audio, MeetingChannel.Microphone, 0, cancellationToken);

    /// <summary>The pieces of one channel from <paramref name="startFrame"/> on, in frames of the file.</summary>
    public static IReadOnlyList<PieceRange> Plan(
        string audio, MeetingChannel channel, int startFrame, CancellationToken cancellationToken = default)
    {
        int frames = (int)Math.Min(int.MaxValue, MeetingWriter.FrameCountAt(audio));
        var reader = new MeetingPcmWindowReader(audio);
        var planner = new FilePiecePlanner();
        int position = Math.Max(0, startFrame);
        while (position < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(MeetingWriter.SampleRate * 4, frames - position);
            planner.Push(reader.Read(channel, position, count));
            position += count;
        }

        int offset = Math.Max(0, startFrame);
        return [.. planner.Finish().Select(range => new PieceRange(range.Start + offset, range.End + offset))];
    }
}
