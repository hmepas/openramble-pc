using System.Globalization;
using System.Text;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// The one place a dictation's numbers are turned into text.
///
/// Two emitters existed briefly upstream - the system log and the shareable file - with their
/// own formats, which is two parsers to keep correct and two chances for one of them to be
/// quietly wrong. There has already been a parser in that repository reading the word "path"
/// as a duration.
///
/// <c>key=value</c>, always, including for stages that did not run: <c>absent</c> says so
/// outright, because a stage nobody measured must never read as <c>0.00</c>.
/// </summary>
/// <remarks>
/// The stage breakdown itself (<c>DictationPhaseBreakdown</c>) lands in DictationCore with the
/// streamed-recognition port; this formatter takes the numbers through <see cref="Stages"/> so
/// the line's shape is pinned independently of that type. Whoever assembles the report maps one
/// onto the other, field for field, and a stage that did not run stays <c>null</c>.
/// </remarks>
public static class DictationSpeedLine
{
    /// <summary>
    /// What the line is made of. Every duration is optional for the same reason: the report
    /// must be able to say a stage did not run.
    /// </summary>
    /// <param name="ReturnFrameWasMainThread">
    /// Kept from the original for the parser's sake. <c>null</c> - not witnessed.
    /// </param>
    /// <param name="StreamedSegments">
    /// How many pieces were recognized while the person was still speaking. Zero is an ordinary
    /// answer: a short take has nothing to stream.
    /// </param>
    public sealed record Stages(
        TimeSpan Total,
        TimeSpan? Paste = null,
        TimeSpan? CaptureFreeze = null,
        TimeSpan? EnginePreparation = null,
        TimeSpan? Recognition = null,
        TimeSpan? RecordingReadable = null,
        TimeSpan? AudioDecoding = null,
        TimeSpan? ExecutorHandover = null,
        TimeSpan? EngineTransport = null,
        TimeSpan? PoolReturn = null,
        TimeSpan? MainActorReturn = null,
        TimeSpan? EngineDispatch = null,
        bool? ReturnFrameWasMainThread = null,
        TimeSpan? EngineQueueing = null,
        TimeSpan? EngineProcessing = null,
        TimeSpan? AudioDuration = null,
        int StreamedSegments = 0);

    /// <summary>The headline marks alone, for a report built without a breakdown.</summary>
    public static Stages StagesOf(DictationSpeedReport report) =>
        new(report.ToRecognizedText, report.ToPasteDispatched);

    public static string Text(Stages stages)
    {
        var line = new StringBuilder("dictation");
        Append(line, "total", stages.Total);
        Append(line, "paste", stages.Paste);
        Append(line, "freeze", stages.CaptureFreeze);
        Append(line, "prepare", stages.EnginePreparation);
        Append(line, "recognize", stages.Recognition);
        // Which branch the take took. Without it a zero cannot be told from a measurement of
        // code that never ran - the mistake that cost four rounds of the upstream investigation.
        line.Append(" path=").Append(stages.RecordingReadable is null ? "memory" : "file");
        Append(line, "readable", stages.RecordingReadable);
        Append(line, "decode", stages.AudioDecoding);
        // Reaching the engine, engine time removed. A large number here means the take was
        // waiting to run rather than running. Split, because the two halves need different fixes.
        Append(line, "handover", stages.ExecutorHandover);
        Append(line, "transport", stages.EngineTransport);
        Append(line, "poolreturn", stages.PoolReturn);
        Append(line, "mainreturn", stages.MainActorReturn);
        Append(line, "enginedispatch", stages.EngineDispatch);
        line.Append(" frame=").Append(stages.ReturnFrameWasMainThread switch
        {
            null => "absent",
            true => "main",
            false => "pool",
        });
        Append(line, "queued", stages.EngineQueueing);
        Append(line, "engine", stages.EngineProcessing);
        Append(line, "audio", stages.AudioDuration);
        // How much of the take was already recognized before the key came up.
        line.Append(" streamed=").Append(stages.StreamedSegments.ToString(CultureInfo.InvariantCulture));
        return line.ToString();
    }

    private static void Append(StringBuilder line, string name, TimeSpan? duration)
    {
        line.Append(' ').Append(name).Append('=');
        if (duration is not { } span)
        {
            line.Append("absent");
            return;
        }

        line.Append(span.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append('s');
    }
}
