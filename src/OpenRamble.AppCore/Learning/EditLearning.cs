using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Reading the field into which the dictation was just inserted.
///
/// This is the only thing the application reads out of other people's windows: the value of
/// exactly the field it inserted the text into itself, and only inside the learning window, in
/// order to learn from the person's edits. Nothing else on screen is ever read.
///
/// An edge of the system, not a detail: substituted like the microphone and the insertion. The
/// promise "switched off means the field is not read" is otherwise unverifiable - and it is
/// exactly the unverifiability that let it be untrue for six months in the original: the
/// switch existed only in a comment.
/// </summary>
public interface IFocusedFieldReading
{
    /// <summary>A snapshot of the focused field. <c>null</c> - there is no field, or it is not readable.</summary>
    FocusedFieldHandle? CaptureFocusedField();
}

/// <summary>Field handle: rereads the value while the field is alive.</summary>
public sealed class FocusedFieldHandle
{
    private readonly Func<string?> read;

    public FocusedFieldHandle(Func<string?> read)
    {
        this.read = read;
    }

    public string? Value() => read();
}

/// <summary>
/// When the observer rereads the field it inserted into.
///
/// The numbers are not an implementation detail: the settings say "twice — at 8 and 25
/// seconds", and the switch's own description says "only in the first half a minute". This is
/// the one place where the application reads inside someone else's window, so the word about
/// WHEN must be exactly true. Spent as a chain of pauses, these same two numbers would take the
/// second reading to the 33rd second - past the promised half a minute, and past what the
/// person agreed to when they turned the switch on. Hence offsets from the insertion, and hence
/// a separate type: a place where the promise and the code are one line apart.
/// </summary>
public static class EditLearningSchedule
{
    /// <summary>Offsets from the moment of insertion, in order.</summary>
    public static IReadOnlyList<TimeSpan> CheckOffsets { get; } =
        [TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(25)];

    /// <summary>The boundary promised in the settings: nothing is read later.</summary>
    public static TimeSpan Limit { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Turn offsets from the insertion into the pauses a sequential loop sleeps.</summary>
    public static IReadOnlyList<TimeSpan> Waits(IReadOnlyList<TimeSpan> offsets)
    {
        var waits = new List<TimeSpan>(offsets.Count);
        TimeSpan elapsed = TimeSpan.Zero;
        foreach (TimeSpan offset in offsets)
        {
            TimeSpan wait = offset - elapsed;
            waits.Add(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
            elapsed = elapsed > offset ? elapsed : offset;
        }

        return waits;
    }
}

/// <summary>
/// Edit observer: rereads the field twice after the insertion and turns an edit of the inserted
/// fragment into a training signal.
///
/// This is decoration on top of the dictation: any failure - the field disappears, the value is
/// unreadable, the whole fragment is rewritten - simply ends the observation. A missing lesson
/// shows up as a dictionary that did not grow; it must never break a dictation.
/// </summary>
public sealed class EditLearningWatcher
{
    private readonly IFocusedFieldReading reader;
    private readonly IReadOnlyList<TimeSpan> checkOffsets;
    private CancellationTokenSource? watch;

    public EditLearningWatcher(IFocusedFieldReading reader, IReadOnlyList<TimeSpan>? checkOffsets = null)
    {
        this.reader = reader;
        this.checkOffsets = checkOffsets ?? EditLearningSchedule.CheckOffsets;
    }

    /// <summary>
    /// Start observing the text just inserted.
    ///
    /// A new insertion cancels the previous observation: only what the person is editing right
    /// now can be learned from.
    /// </summary>
    public void BeginWatching(string inserted, Action<string, string> onEdit)
    {
        Cancel();
        FocusedFieldHandle? field = reader.CaptureFocusedField();
        if (field is null)
        {
            return;
        }

        if (field.Value() is not string baseline || !baseline.Contains(inserted, StringComparison.Ordinal))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        watch = cancellation;
        _ = Observe(field, baseline, inserted, onEdit, cancellation.Token);
    }

    public void Cancel()
    {
        watch?.Cancel();
        watch = null;
    }

    private async Task Observe(
        FocusedFieldHandle field,
        string baseline,
        string inserted,
        Action<string, string> onEdit,
        CancellationToken token)
    {
        foreach (TimeSpan wait in EditLearningSchedule.Waits(checkOffsets))
        {
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (field.Value() is not string current)
            {
                return;
            }

            if (InsertedEditExtractor.Extract(baseline, current, inserted) is InsertedEditExtractor.Edit edit)
            {
                onEdit(edit.Original, edit.Edited);
                return;
            }
        }
    }
}
