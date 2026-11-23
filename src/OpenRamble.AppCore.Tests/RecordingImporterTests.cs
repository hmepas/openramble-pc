using System.Diagnostics;
using DictationCore;
using DictationCore.Audio;
using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Audio that the file planner takes for speech: noise whose loudness changes every 20 ms, as
/// syllables do. A steady tone would read as the room's floor.
/// </summary>
internal static class SpeechLikeAudio
{
    public static float Sample(int index, float level = 0.1f)
    {
        unchecked
        {
            uint state = ((uint)(index / 320) * 2_654_435_761u) + 1;
            state = (state * 1_664_525u) + 1_013_904_223u;
            float loudness = 0.4f + (1.2f * (state >> 8) / (1 << 24));
            uint noise = ((uint)index * 747_796_405u) + 2_891_336_453u;
            noise ^= noise >> 13;
            noise *= 1_274_126_177u;
            return level * loudness * (((float)(noise >> 8) / (1 << 23)) - 1);
        }
    }

    /// <summary>A mono 16 kHz file, <paramref name="seconds"/> long, speaking where <paramref name="speaks"/> says so.</summary>
    public static void Write(string path, int seconds, Func<int, bool>? speaks = null)
    {
        var writer = new WAVWriter(path);
        writer.Open();
        for (int second = 0; second < seconds; second++)
        {
            bool voiced = speaks?.Invoke(second) ?? true;
            float[] buffer = new float[16_000];
            if (voiced)
            {
                for (int index = 0; index < buffer.Length; index++) { buffer[index] = Sample((second * 16_000) + index); }
            }

            writer.Append(buffer);
        }

        writer.Close();
    }
}

/// <summary>The shared engine, scripted: "Piece N." for the Nth decode.</summary>
internal sealed class ScriptedImportRecognizer : IImportRecognizer
{
    private readonly Lock gate = new();
    private readonly List<int> decodedSamples = [];
    private TaskCompletionSource? held;
    private bool holdsNextDecode;
    private int decodes;

    public bool FailsEveryDecode { get; set; }

    public int Decodes { get { lock (gate) { return decodes; } } }

    public IReadOnlyList<int> DecodedSamples { get { lock (gate) { return [.. decodedSamples]; } } }

    public bool IsDecodeWaiting { get { lock (gate) { return held is not null; } } }

    public void HoldNextDecode() { lock (gate) { holdsNextDecode = true; } }

    public void ReleaseDecode()
    {
        TaskCompletionSource? waiting;
        lock (gate)
        {
            waiting = held;
            held = null;
        }

        waiting?.SetResult();
    }

    public async Task<string> Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        Task? wait = null;
        int number;
        lock (gate)
        {
            decodes++;
            number = decodes;
            decodedSamples.Add(samples.Length);
            if (holdsNextDecode)
            {
                holdsNextDecode = false;
                held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = held.Task;
            }
        }

        if (wait is not null) { await wait; }
        if (FailsEveryDecode) { throw new ASREngineError.InferenceFailed("scripted"); }
        return $"Piece {number}.";
    }
}

/// <summary>Someone dictating, as the importer sees it through <see cref="DictationPriority"/>.</summary>
internal sealed class FakeDictationTurn
{
    private volatile bool dictating;

    public void SetDictating(bool value) => dictating = value;

    public async Task WaitForTurn(CancellationToken cancellationToken)
    {
        while (dictating) { await Task.Delay(5, cancellationToken); }
    }
}

internal sealed class FakeCompressor(bool succeeds) : IRecordingAudioCompressor
{
    public List<string> Sources { get; } = [];

    public async Task<bool> Compress(string source, string destination, CancellationToken cancellationToken)
    {
        Sources.Add(source);
        if (!succeeds) { return false; }
        await File.WriteAllTextAsync(destination, "aac", cancellationToken);
        return true;
    }
}

/// <summary>
/// A file brought into the Library becomes a recording with its sound, its name and its words,
/// sharing the dictation engine and always after dictation. Ported from upstream
/// RecordingImporterTests.swift; the language tests are not (always automatic here).
/// </summary>
public sealed class RecordingImporterTests : IDisposable
{
    private readonly string root;
    private readonly MeetingStore store;
    private readonly ScriptedImportRecognizer engine = new();
    private readonly FakeDictationTurn turn = new();
    private readonly Lock gate = new();
    private readonly List<MeetingRecordingMetadata> imported = [];
    private readonly List<Guid> finished = [];
    private readonly List<(RecordingImporter.Failure Failure, string Name)> failures = [];
    private readonly Dictionary<Guid, IReadOnlyList<MeetingUtterance>> transcripts = [];
    private volatile bool modelReady = true;
    private RecordingImporter importer;

    public RecordingImporterTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid()}");
        Directory.CreateDirectory(root);
        store = new MeetingStore(Path.Combine(root, "Recordings"), new DeletingTrasher());
        importer = MakeImporter();
    }

    public void Dispose()
    {
        importer.Stop();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class DeletingTrasher : IRecordingTrasher
    {
        public bool IsAvailable => true;

        public void Trash(string path) => Directory.Delete(path, recursive: true);
    }

    private RecordingImporter MakeImporter(IRecordingAudioCompressor? compressor = null)
    {
        var made = new RecordingImporter(new RecordingImporter.Edges(
            store,
            engine,
            () => modelReady,
            () => new TextPipeline())
        {
            WaitForTurn = turn.WaitForTurn,
            Compressor = compressor,
        });
        made.OnImported = metadata => { lock (gate) { imported.Add(metadata); } };
        made.OnFinished = id => { lock (gate) { finished.Add(id); } };
        made.OnFailure = (failure, name) => { lock (gate) { failures.Add((failure, name)); } };
        made.OnTranscript = (id, utterances) => { lock (gate) { transcripts[id] = utterances; } };
        return made;
    }

    private List<MeetingRecordingMetadata> Imported { get { lock (gate) { return [.. imported]; } } }

    private List<Guid> Finished { get { lock (gate) { return [.. finished]; } } }

    private List<(RecordingImporter.Failure Failure, string Name)> Failures { get { lock (gate) { return [.. failures]; } } }

    private static async Task Wait(string what, Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (done()) { return; }
            await Task.Delay(5);
        }

        Assert.Fail($"never happened: {what}");
    }

    /// <summary>
    /// 70 s: fifteen seconds of speech, a long silence, fifteen more. Each stretch fits one piece,
    /// so there are two pieces and two paragraphs.
    /// </summary>
    private string TwoPartFile(string name = "Lecture.wav")
    {
        string path = Path.Combine(root, name);
        SpeechLikeAudio.Write(path, 70, second => second < 15 || (second >= 50 && second < 65));
        return path;
    }

    private string ShortFile(string name, int seconds = 5)
    {
        string path = Path.Combine(root, name);
        SpeechLikeAudio.Write(path, seconds);
        return path;
    }

    // A file becomes a recording

    [Fact]
    public async Task AFileBecomesARecordingWithItsSoundItsNameAndItsWords()
    {
        string file = TwoPartFile();
        byte[] original = File.ReadAllBytes(file);

        await importer.Add([file]);
        await Wait("the transcript", () => Finished.Count == 1);

        MeetingRecordingMetadata recording = Assert.Single(store.List());
        Assert.Equal([recording.Id], Imported.Select(metadata => metadata.Id));
        Assert.Equal("Lecture", recording.Title);
        Assert.Equal(70, recording.Duration, 2);
        Assert.True(recording.IsImported);
        Assert.Equal(RecordingImportLanguage.Automatic, recording.ImportLanguage);
        Assert.False(recording.IsMeeting);
        Assert.Equal(MeetingTranscriptionState.Complete, recording.TranscriptionState);

        // Its sound is in the store, in the store's own format, and the file is only read.
        string audio = Assert.IsType<string>(store.RawAudioPath(recording.Id));
        Assert.Equal(70 * 16_000, MeetingWriter.FrameCountAt(audio));
        Assert.NotNull(store.PeaksPath(recording.Id));
        Assert.Equal(original, File.ReadAllBytes(file));

        // Two stretches of speech, a long silence between: two paragraphs, each where its speech is.
        IReadOnlyList<MeetingUtterance> utterances = store.Transcript(recording.Id)!.Utterances;
        Assert.Equal(["Piece 1.", "Piece 2."], utterances.Select(utterance => utterance.Text));
        lock (gate) { Assert.Equal(["Piece 1.", "Piece 2."], transcripts[recording.Id].Select(utterance => utterance.Text)); }
        Assert.Equal(0, utterances[0].Start, 2);
        Assert.InRange(utterances[1].Start, 47.5, 50.5);
        Assert.All(utterances, utterance => Assert.Equal(MeetingChannel.Microphone, utterance.Channel));

        // The silence was never sent to the engine.
        Assert.True(engine.DecodedSamples.Sum() < 36 * 16_000);
        await Wait("the importer to rest", () => importer.Activity.IsIdle);
    }

    [Fact]
    public async Task AShortFileIsOnePiece()
    {
        await importer.Add([ShortFile("Note.wav")]);
        await Wait("the transcript", () => Finished.Count == 1);

        MeetingRecordingMetadata recording = Assert.Single(store.List());
        Assert.Equal(["Piece 1."], store.Transcript(recording.Id)!.Utterances.Select(utterance => utterance.Text));
        Assert.Equal([5 * 16_000], engine.DecodedSamples);
    }

    [Fact]
    public async Task AFileWithNoSpeechIsKeptAndSaysSo()
    {
        string file = Path.Combine(root, "Silence.wav");
        SpeechLikeAudio.Write(file, 90, _ => false);

        await importer.Add([file]);
        await Wait("the end", () => Finished.Count == 1);

        MeetingRecordingMetadata recording = Assert.Single(store.List());
        Assert.Equal(MeetingTranscriptionState.Complete, recording.TranscriptionState);
        Assert.Empty(store.Transcript(recording.Id)!.Utterances);
        Assert.Equal(0, engine.Decodes);
    }

    // What cannot be read

    [Fact]
    public async Task WhatCannotBeReadIsSaidAndLeavesNothingBehind()
    {
        string file = Path.Combine(root, "notes.m4a");
        File.WriteAllText(file, "not audio at all");

        await importer.Add([file]);

        Assert.Equal([(RecordingImporter.Failure.Unreadable, "notes")], Failures);
        Assert.Empty(store.List());
        Assert.Empty(Directory.Exists(store.IncompleteRoot) ? Directory.GetFileSystemEntries(store.IncompleteRoot) : []);
        Assert.True(importer.Activity.IsIdle);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AFileWithNoSoundAtAllIsSaidToBeEmpty()
    {
        string file = Path.Combine(root, "Nothing.wav");
        SpeechLikeAudio.Write(file, 0);

        await importer.Add([file]);

        Assert.Equal([(RecordingImporter.Failure.Empty, "Nothing")], Failures);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task OneBadFileDoesNotStopTheOthers()
    {
        string bad = Path.Combine(root, "broken.mp3");
        File.WriteAllText(bad, "nope");
        string good = ShortFile("Good.wav");

        await importer.Add([bad, good]);
        await Wait("the good one", () => Finished.Count == 1);

        Assert.Equal(["broken"], Failures.Select(failure => failure.Name));
        Assert.Equal(["Good"], store.List().Select(recording => recording.Title));
    }

    // Which model

    [Fact]
    public async Task AFileWaitsForTheModelAndStartsWhenItIsReady()
    {
        modelReady = false;
        await importer.Add([ShortFile("Later.wav")]);
        await Task.Delay(60);

        Guid id = Assert.Single(Imported).Id;
        Assert.Equal([id], importer.Activity.Waiting);
        Assert.True(importer.Activity.NeedsModel);
        Assert.Null(importer.Activity.Transcribing);
        Assert.Equal(0, engine.Decodes);
        Assert.Equal(MeetingTranscriptionState.Live, store.Metadata(id)!.TranscriptionState);

        modelReady = true;
        importer.ModelsChanged();
        await Wait("the transcript", () => Finished.SequenceEqual([id]));
        Assert.False(importer.Activity.NeedsModel);
    }

    [Fact]
    public async Task AModelThatGoesAwayMidFileStartsTheFileOverWhenItIsBack()
    {
        engine.HoldNextDecode();
        await importer.Add([TwoPartFile()]);
        await Wait("the first decode", () => engine.IsDecodeWaiting);
        Guid id = Assert.Single(Imported).Id;

        modelReady = false;
        engine.ReleaseDecode();
        await Wait("the file back in line", () => importer.Activity.Waiting.SequenceEqual([id]) && importer.Activity.Transcribing is null);
        Assert.True(importer.Activity.NeedsModel);
        Assert.Equal(1, engine.Decodes);
        Assert.Equal(MeetingTranscriptionState.Live, store.Metadata(id)!.TranscriptionState);

        modelReady = true;
        importer.ModelsChanged();
        await Wait("the transcript", () => Finished.Count == 1);
        Assert.Equal(3, engine.Decodes);
        Assert.Equal(MeetingTranscriptionState.Complete, store.Metadata(id)!.TranscriptionState);
    }

    // One at a time, dictation first

    [Fact]
    public async Task FilesAreTranscribedOneAtATimeInTheOrderTheyCame()
    {
        string first = ShortFile("First.wav");
        string second = ShortFile("Second.wav");
        engine.HoldNextDecode();

        await importer.Add([first, second]);
        await Wait("the first decode", () => engine.IsDecodeWaiting);

        List<MeetingRecordingMetadata> files = Imported;
        Assert.Equal(["First", "Second"], files.Select(file => file.Title));
        Assert.Equal(files[0].Id, importer.Activity.Transcribing);
        Assert.Equal([files[1].Id], importer.Activity.Waiting);

        engine.ReleaseDecode();
        await Wait("both transcripts", () => Finished.Count == 2);
        Assert.Equal(files.Select(file => file.Id), Finished);
    }

    [Fact]
    public async Task APieceDoesNotStartWhileSomeoneDictates()
    {
        turn.SetDictating(true);
        await importer.Add([ShortFile("Busy.wav")]);
        await Task.Delay(80);

        Assert.Equal(0, engine.Decodes);
        Assert.Equal(Assert.Single(Imported).Id, importer.Activity.Transcribing);

        turn.SetDictating(false);
        await Wait("the transcript", () => Finished.Count == 1);
    }

    // Endings

    [Fact]
    public async Task MovingAFileToTheTrashStopsItsTranscript()
    {
        engine.HoldNextDecode();
        await importer.Add([TwoPartFile()]);
        await Wait("the first decode", () => engine.IsDecodeWaiting);
        Guid id = Assert.Single(Imported).Id;

        importer.Cancel(id);
        store.Trash(id);
        engine.ReleaseDecode();
        await Wait("the importer to rest", () => importer.Activity.IsIdle);

        Assert.Empty(Finished);
        Assert.Empty(store.List());
        Assert.Equal(1, engine.Decodes);
    }

    [Fact]
    public async Task AWaitingFileCanBeRemovedBeforeItsTurn()
    {
        engine.HoldNextDecode();
        await importer.Add([ShortFile("First.wav"), ShortFile("Second.wav")]);
        await Wait("the first decode", () => engine.IsDecodeWaiting);
        List<MeetingRecordingMetadata> files = Imported;

        importer.Cancel(files[1].Id);
        store.Trash(files[1].Id);
        engine.ReleaseDecode();
        await Wait("the importer to rest", () => importer.Activity.IsIdle && Finished.Count == 1);

        Assert.Equal([files[0].Id], Finished);
    }

    /// <summary>
    /// Trashed while its decode was in flight, and the model went away under that decode: the
    /// file must not go back in line for a recording that is gone.
    /// </summary>
    [Fact]
    public async Task AFileTrashedAsTheModelGoesAwayIsNotTakenUpAgain()
    {
        engine.HoldNextDecode();
        await importer.Add([TwoPartFile()]);
        await Wait("the first decode", () => engine.IsDecodeWaiting);
        Guid id = Assert.Single(Imported).Id;

        importer.Cancel(id);
        store.Trash(id);
        modelReady = false;
        engine.FailsEveryDecode = true;
        engine.ReleaseDecode();
        await Wait("the decode to end", () => importer.Activity.Transcribing is null);

        Assert.Empty(importer.Activity.Waiting);
        engine.FailsEveryDecode = false;
        modelReady = true;
        importer.ModelsChanged();
        await Task.Delay(80);
        Assert.Empty(Finished);
        Assert.Equal(1, engine.Decodes);
    }

    [Fact]
    public async Task ThreeFailuresInARowStopAndSayTheTranscriptIsNotWhole()
    {
        // 100 s of unbroken speech: more than three pieces.
        engine.FailsEveryDecode = true;
        await importer.Add([ShortFile("Long.wav", seconds: 100)]);
        await Wait("the end", () => Finished.Count == 1);

        MeetingRecordingMetadata recording = Assert.Single(store.List());
        Assert.Equal(MeetingTranscriptionState.Partial, recording.TranscriptionState);
        Assert.Equal(RecordingImporter.FailuresBeforeStop, engine.Decodes);
        Assert.NotNull(store.RawAudioPath(recording.Id));
    }

    [Fact]
    public async Task ARecordingWhoseSoundIsGoneFails()
    {
        modelReady = false;
        await importer.Add([ShortFile("Gone.wav")]);
        Guid id = Assert.Single(Imported).Id;
        File.Delete(store.RawAudioPath(id)!);

        modelReady = true;
        importer.ModelsChanged();
        await Wait("the end", () => Finished.Count == 1);

        Assert.Equal(MeetingTranscriptionState.Failed, store.Metadata(id)!.TranscriptionState);
    }

    // Compression

    [Fact]
    public async Task AWholeTranscriptCompactsTheSoundAndKeepsTheWaveform()
    {
        var compressor = new FakeCompressor(succeeds: true);
        importer = MakeImporter(compressor);
        await importer.Add([ShortFile("Note.wav")]);
        await Wait("the transcript", () => Finished.Count == 1);

        Guid id = Assert.Single(store.List()).Id;
        Assert.Single(compressor.Sources);
        Assert.Null(store.RawAudioPath(id));
        Assert.NotNull(store.CompressedAudioPath(id));
        Assert.NotNull(store.PeaksPath(id));
        Assert.False(File.Exists(Path.Combine(store.Directory(id), "audio.m4a.incomplete")));
    }

    [Fact]
    public async Task AFailedCompressionKeepsTheWav()
    {
        importer = MakeImporter(new FakeCompressor(succeeds: false));
        await importer.Add([ShortFile("Note.wav")]);
        await Wait("the transcript", () => Finished.Count == 1);

        Guid id = Assert.Single(store.List()).Id;
        Assert.NotNull(store.RawAudioPath(id));
        Assert.Null(store.CompressedAudioPath(id));
        Assert.Equal(MeetingTranscriptionState.Complete, store.Metadata(id)!.TranscriptionState);
    }

    [Fact]
    public async Task APartialTranscriptKeepsTheWav()
    {
        var compressor = new FakeCompressor(succeeds: true);
        importer = MakeImporter(compressor);
        engine.FailsEveryDecode = true;
        await importer.Add([ShortFile("Long.wav", seconds: 100)]);
        await Wait("the end", () => Finished.Count == 1);

        Assert.Empty(compressor.Sources);
        Assert.NotNull(store.RawAudioPath(Assert.Single(store.List()).Id));
    }

    [Fact]
    public async Task FfmpegWritesAnArchiveThatDecodesToTheSameLength()
    {
        var compressor = new FfmpegAudioCompressor();
        if (!compressor.IsAvailable) { return; }

        string directory = Path.Combine(root, "archive");
        Directory.CreateDirectory(directory);
        var writer = new MeetingWriter(directory);
        writer.Open();
        float[] voice = Enumerable.Range(0, 3 * 16_000).Select(index => SpeechLikeAudio.Sample(index)).ToArray();
        writer.Append(voice, new float[voice.Length]);
        writer.Finish();

        string archive = Path.Combine(directory, "audio.m4a.incomplete");
        Assert.True(await compressor.Compress(writer.AudioPath, archive, CancellationToken.None));
        Assert.True(new FileInfo(archive).Length > 0);
        Assert.True(new FileInfo(archive).Length < new FileInfo(writer.AudioPath).Length / 4);

        Assert.False(new FfmpegAudioCompressor(searchPath: root).IsAvailable);
        Assert.False(await new FfmpegAudioCompressor(searchPath: root).Compress(writer.AudioPath, archive + "2", CancellationToken.None));
    }

    // A quit

    [Fact]
    public async Task AnImportAQuitInterruptedIsTakenUpAgain()
    {
        // What a quit leaves: the sound in the store, marked as still to be transcribed.
        MeetingRecordingMetadata left = RecordingImporter.Store(TwoPartFile(), store, DateTimeOffset.Now);
        Assert.Equal(MeetingTranscriptionState.Live, left.TranscriptionState);
        // And one that was finished long ago: not touched again.
        MeetingRecordingMetadata done = RecordingImporter.Store(ShortFile("Done.wav"), store, DateTimeOffset.Now);
        done.TranscriptionState = MeetingTranscriptionState.Complete;
        store.Write(done);

        importer.Resume(store.List());
        await Wait("the transcript", () => Finished.Count == 1);

        Assert.Equal([left.Id], Finished);
        Assert.Equal(MeetingTranscriptionState.Complete, store.Metadata(left.Id)!.TranscriptionState);
        Assert.Equal(["Piece 1.", "Piece 2."], store.Transcript(left.Id)!.Utterances.Select(utterance => utterance.Text));
    }

    /// <summary>
    /// A quit during compaction leaves a whole transcript beside its WAV and a half archive: the
    /// half archive goes and the sound is compacted at the next launch.
    /// </summary>
    [Fact]
    public async Task AQuitDuringCompactionIsCompactedAtTheNextLaunch()
    {
        MeetingRecordingMetadata whole = RecordingImporter.Store(ShortFile("Whole.wav"), store, DateTimeOffset.Now);
        whole.TranscriptionState = MeetingTranscriptionState.Complete;
        store.Write(whole);
        MeetingRecordingMetadata partial = RecordingImporter.Store(ShortFile("Partial.wav"), store, DateTimeOffset.Now);
        partial.TranscriptionState = MeetingTranscriptionState.Partial;
        store.Write(partial);
        string halfArchive = Path.Combine(store.Directory(partial.Id), "audio.m4a.incomplete");
        await File.WriteAllTextAsync(halfArchive, "half");

        importer.Stop();
        var compressor = new FakeCompressor(succeeds: true);
        importer = MakeImporter(compressor);
        importer.Resume(store.List());
        await Wait("the archive", () => store.CompressedAudioPath(whole.Id) is not null && store.RawAudioPath(whole.Id) is null);

        Assert.False(File.Exists(halfArchive));
        Assert.NotNull(store.RawAudioPath(partial.Id));
        Assert.Single(compressor.Sources);
        Assert.Equal(0, engine.Decodes);
    }

    /// <summary>
    /// A live recording as a quit or a crash leaves it: both channels in the store, published,
    /// not an import, with whatever transcript the live queue had written.
    /// </summary>
    private MeetingRecordingMetadata StoreMeeting(
        DateTimeOffset startedAt,
        Func<int, bool> microphoneSpeaks,
        Func<int, bool>? systemSpeaks,
        MeetingTranscriptionState state,
        MeetingTranscript? transcript = null,
        int seconds = 70)
    {
        var id = Guid.NewGuid();
        string directory = store.IncompleteDirectory(id);
        Directory.CreateDirectory(directory);
        var writer = new MeetingWriter(directory);
        writer.Open();
        for (int second = 0; second < seconds; second++)
        {
            float[] microphone = new float[16_000];
            float[] system = new float[16_000];
            for (int index = 0; index < 16_000; index++)
            {
                int at = (second * 16_000) + index;
                if (microphoneSpeaks(second)) { microphone[index] = SpeechLikeAudio.Sample(at); }
                if (systemSpeaks?.Invoke(second) == true) { system[index] = SpeechLikeAudio.Sample(at + 7); }
            }

            writer.Append(microphone, system);
        }

        writer.Finish();
        MeetingRecordingMetadata metadata = MeetingRecordingMetadata.Create(
            startedAt: startedAt,
            systemAudio: new SystemAudioSummary(wasRequested: true, everDeliveredBuffers: true, everDeliveredAudio: systemSpeaks is not null),
            id: id,
            duration: seconds,
            endReason: MeetingEndReason.StoppedByUser,
            transcriptionState: state);
        store.Write(metadata, incomplete: true);
        if (transcript is not null) { store.Write(transcript, id, incomplete: true); }
        store.Publish(id);
        return metadata;
    }

    /// <summary>
    /// A meeting a quit stopped before its transcript caught up is finished at the next launch:
    /// only the audio not yet decoded, on every channel that had sound, after what was written,
    /// oldest first.
    /// </summary>
    [Fact]
    public async Task AMeetingAQuitLeftUntranscribedIsFinishedAtTheNextLaunch()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        // Decoded up to 20 s on the microphone, nothing yet from the other side.
        MeetingRecordingMetadata newer = StoreMeeting(
            now,
            second => second < 15 || (second >= 50 && second < 65),
            second => second >= 30 && second < 45,
            MeetingTranscriptionState.Live,
            new MeetingTranscript(
                [new MeetingUtterance(MeetingChannel.Microphone, 0, 15, "Earlier.")],
                new Dictionary<MeetingChannel, int> { [MeetingChannel.Microphone] = 20 * 16_000 }));
        // Crash-recovered with no transcript at all, and no sound ever came from the other side.
        MeetingRecordingMetadata older = StoreMeeting(
            now.AddHours(-1),
            second => second < 5,
            systemSpeaks: null,
            MeetingTranscriptionState.Partial,
            seconds: 10);

        importer.Resume(store.List());
        await Wait("both transcripts", () => Finished.Count == 2);

        Assert.Equal([older.Id, newer.Id], Finished);
        Assert.Equal(MeetingTranscriptionState.Complete, store.Metadata(older.Id)!.TranscriptionState);
        Assert.Equal(MeetingTranscriptionState.Complete, store.Metadata(newer.Id)!.TranscriptionState);
        MeetingUtterance only = Assert.Single(store.Transcript(older.Id)!.Utterances);
        Assert.Equal(MeetingChannel.Microphone, only.Channel);

        MeetingTranscript resumed = store.Transcript(newer.Id)!;
        Assert.Equal("Earlier.", resumed.Utterances[0].Text);
        Assert.Contains(resumed.Utterances, utterance => utterance.Channel == MeetingChannel.System);
        Assert.All(
            resumed.Utterances.Skip(1).Where(utterance => utterance.Channel == MeetingChannel.Microphone),
            utterance => Assert.True(utterance.Start >= 20));
        Assert.Equal(70 * 16_000, resumed.DecodedFrames[MeetingChannel.Microphone]);
        Assert.Equal(70 * 16_000, resumed.DecodedFrames[MeetingChannel.System]);
        // The older recording's one piece, then one per channel of the newer: never the first 20 s again.
        Assert.Equal(3, engine.Decodes);
    }

    /// <summary>
    /// A meeting whose transcript is whole but whose WAV a quit kept is compacted at the next
    /// launch, like an import; one whose transcript already covers its audio is not decoded again.
    /// </summary>
    [Fact]
    public async Task AWholeMeetingStillHoldingItsWavIsCompactedAtTheNextLaunch()
    {
        MeetingRecordingMetadata whole = StoreMeeting(
            DateTimeOffset.Now, second => second < 5, systemSpeaks: null, MeetingTranscriptionState.Complete, seconds: 10);
        MeetingRecordingMetadata covered = StoreMeeting(
            DateTimeOffset.Now,
            second => second < 5,
            systemSpeaks: null,
            MeetingTranscriptionState.Partial,
            new MeetingTranscript(
                [new MeetingUtterance(MeetingChannel.Microphone, 0, 5, "Failed.", isFailed: true)],
                new Dictionary<MeetingChannel, int> { [MeetingChannel.Microphone] = 10 * 16_000 }),
            seconds: 10);

        importer.Stop();
        var compressor = new FakeCompressor(succeeds: true);
        importer = MakeImporter(compressor);
        importer.Resume(store.List());
        await Wait("the archive", () => store.CompressedAudioPath(whole.Id) is not null && store.RawAudioPath(whole.Id) is null);
        await Task.Delay(100);

        Assert.Equal(0, engine.Decodes);
        Assert.Equal(MeetingTranscriptionState.Partial, store.Metadata(covered.Id)!.TranscriptionState);
        Assert.NotNull(store.RawAudioPath(covered.Id));
    }

    /// <summary>
    /// A quit while a file is still being copied in leaves half of its sound. That must not come
    /// back as a recording: the file itself is untouched, and half of it under no name would
    /// only confuse.
    /// </summary>
    [Fact]
    public void AHalfCopiedFileIsDroppedAndNeverComesBackAsARecording()
    {
        string half = RecordingImporter.CopyDirectory(Guid.NewGuid(), store);
        Directory.CreateDirectory(half);
        var writer = new MeetingWriter(half);
        writer.Open();
        float[] voice = Enumerable.Range(0, 16_000).Select(index => SpeechLikeAudio.Sample(index)).ToArray();
        writer.Append(voice, new float[voice.Length]);
        writer.Abandon();

        // The store's own crash recovery, which runs first at launch.
        Assert.Empty(store.RecoverIncomplete());
        Assert.True(Directory.Exists(half));

        importer.Resume(store.List());

        Assert.False(Directory.Exists(half));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task StoppingForAQuitLeavesTheRecordingMarkedUnfinished()
    {
        engine.HoldNextDecode();
        await importer.Add([TwoPartFile()]);
        await Wait("the first decode", () => engine.IsDecodeWaiting);
        Guid id = Assert.Single(Imported).Id;

        importer.Stop();
        engine.ReleaseDecode();
        await Task.Delay(100);

        Assert.Empty(Finished);
        Assert.Equal(MeetingTranscriptionState.Live, store.Metadata(id)!.TranscriptionState);
        Assert.Null(store.Transcript(id));
        Assert.NotNull(store.RawAudioPath(id));
    }
}
