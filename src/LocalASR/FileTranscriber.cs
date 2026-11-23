using DictationCore;

namespace LocalASR;

/// <summary>A whole file's transcript: its text, its words timed against the file, and its length.</summary>
/// <remarks>
/// The Swift original also carries the decoding and dispatch times its benchmark reads; nothing
/// here reads them, so they are not ported.
/// </remarks>
public sealed record FileTranscript(
    string Text,
    IReadOnlyList<ASRResult.Word> Words,
    TimeSpan Duration,
    TimeSpan RecognitionDuration)
{
    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(FileTranscript? other) =>
        other is not null
        && Text == other.Text
        && Words.SequenceEqual(other.Words)
        && Duration == other.Duration
        && RecognitionDuration == other.RecognitionDuration;

    public override int GetHashCode() => HashCode.Combine(Text, Words.Count, Duration);
}

/// <summary>What became of one file: Swift's <c>Result&lt;FileTranscript, Error&gt;</c>.</summary>
public abstract record FileOutcome
{
    private FileOutcome()
    {
    }

    public sealed record Transcribed(FileTranscript Transcript) : FileOutcome;

    public sealed record Failed(Exception Error) : FileOutcome;
}

/// <summary>
/// One model, one inference at a time. Long files never become one PCM array. Failed inputs do
/// not stop other files.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the command-line tool and, later, the Library's file import: progress and
/// cancellation are the caller's, and nothing here writes a file or prints.
/// </para>
/// <para>
/// <b>Deviation from the Swift original:</b> the CPU there prepares the next piece while the GPU
/// runs the current one. Here the engine is on the CPU too, and reading the next piece is a
/// fraction of a second against seconds of recognition, so pieces are read and recognized one
/// after the other. Dictation priority is checked before every piece rather than also inside
/// the engine (see <see cref="LocalTranscriber.TranscribeChunk"/>).
/// </para>
/// </remarks>
public sealed class FileTranscriber
{
    private readonly LocalTranscriber transcriber;
    private readonly DictationPriority? priority;
    private readonly int segmentSeconds;

    public FileTranscriber(LocalTranscriber transcriber, DictationPriority? priority = null, int? segmentSeconds = null)
    {
        int seconds = segmentSeconds ?? FileAudioChunks.DefaultSeconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 15);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 60);
        this.transcriber = transcriber;
        this.priority = priority;
        this.segmentSeconds = seconds;
    }

    /// <summary>
    /// Every file in order. <paramref name="completed"/> hears about each one exactly once, a
    /// failure included; only cancellation ends the run early, and then no further outcome is
    /// published.
    /// </summary>
    /// <param name="progress">The file's index, seconds recognized so far, and its length in seconds.</param>
    public async Task Transcribe(
        IReadOnlyList<string> files,
        Func<int, FileOutcome, Task> completed,
        Action<int, double, double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int file = index;
            FileOutcome outcome;
            try
            {
                FileTranscript transcript = await Transcribe(
                    files[index],
                    progress is null ? null : (seconds, total) => progress(file, seconds, total),
                    cancellationToken).ConfigureAwait(false);
                outcome = new FileOutcome.Transcribed(transcript);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (Interrupted(error, cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception error)
            {
                outcome = new FileOutcome.Failed(error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await completed(index, outcome).ConfigureAwait(false);
        }
    }

    /// <summary>How long a decoder killed by a signal waits for the cancellation it may precede.</summary>
    internal static readonly TimeSpan SignalGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether a failed file is really the job being stopped. Ctrl-C reaches ffmpeg too, and its
    /// exit can be read before this process has cancelled the token - so a decoder that died of a
    /// signal is given a moment for that cancellation to arrive instead of failing the file.
    /// </summary>
    internal static bool Interrupted(Exception error, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        || (error is AudioFileReader.Failure.Unreadable { EndedBySignal: true }
            && cancellationToken.WaitHandle.WaitOne(SignalGrace));

    /// <summary>One file: read in pieces, recognized one piece at a time, joined by appending.</summary>
    public Task<FileTranscript> Transcribe(
        string file,
        Action<double, double>? progress = null,
        CancellationToken cancellationToken = default) =>
        transcriber.WithPreparedModel(
            async generation =>
            {
                using FileAudioChunks chunks = await DedicatedThread.Run(
                    () => FileAudioChunks.Read(file, segmentSeconds, cancellationToken),
                    "OpenRamble file read").ConfigureAwait(false);
                return await Transcribe(chunks, generation, progress, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<FileTranscript> Transcribe(
        FileAudioChunks chunks,
        int generation,
        Action<double, double>? progress,
        CancellationToken cancellationToken)
    {
        var joiner = new TimedTranscriptJoiner();
        var duration = TimeSpan.FromSeconds(chunks.Duration);
        TimeSpan recognition = TimeSpan.Zero;
        while (await DedicatedThread.Run(() => chunks.Next(cancellationToken), "OpenRamble file read")
                   .ConfigureAwait(false) is { } chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (priority is not null)
            {
                // Dictation is interactive and this is not: the piece waits for the take to end.
                await priority.WaitForTurn(cancellationToken).ConfigureAwait(false);
            }

            ASRResult result = await transcriber.TranscribeChunk(chunk.Samples, generation, cancellationToken)
                .ConfigureAwait(false);
            joiner.Append(result, chunk, duration);
            recognition += result.ProcessingDuration;
            progress?.Invoke(
                Math.Min(chunks.Duration, (double)chunk.EndFrame / FilePiecePlanner.SampleRate),
                chunks.Duration);
        }

        return new FileTranscript(joiner.Text, [.. joiner.Words], duration, recognition);
    }
}
