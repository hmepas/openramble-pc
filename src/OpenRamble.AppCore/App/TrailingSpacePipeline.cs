using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Ends the finished text with a space, when the person asked for one.
///
/// A wrapper rather than a stage inside the pipeline, and deliberately: the pipeline is the
/// shared rule set, recorded case by case in the upstream conformance fixtures, and a personal
/// preference has no business inside a recording. The polisher's whole job is to trim trailing
/// whitespace, so this can only come after it - which is exactly what wrapping means.
/// </summary>
/// <remarks>
/// Wraps a function rather than a pipeline type so it composes with whatever produces a
/// <see cref="TextPipeline.RunResult"/> - the package pipeline today, an interface over it
/// tomorrow - without this file knowing which.
/// </remarks>
public sealed class TrailingSpacePipeline : ITextProcessing
{
    private readonly Func<string, TextPipeline.RunResult> wrapped;
    private readonly bool appendsSpace;

    public TrailingSpacePipeline(Func<string, TextPipeline.RunResult> wrapped, bool appendsSpace)
    {
        this.wrapped = wrapped;
        this.appendsSpace = appendsSpace;
    }

    public TrailingSpacePipeline(ITextProcessing wrapped, bool appendsSpace)
        : this(wrapped.Run, appendsSpace)
    {
    }

    public TextPipeline.RunResult Run(string recognized)
    {
        TextPipeline.RunResult run = wrapped(recognized);
        if (!appendsSpace || run.Output.Text.Length == 0) { return run; }
        // Not after a line break: "new line" already ended the text where the person wanted
        // it, and a space after it is an indent nobody asked for.
        if (run.Output.Text.EndsWith('\n')) { return run; }

        var spaced = new TextPipeline.Output(run.Output.Text + " ", run.Output.Command);
        // Provenance follows the text it describes, or the edit watcher would be comparing
        // against something never inserted.
        return new TextPipeline.RunResult(
            spaced,
            new PipelineProvenance(
                run.Provenance.AfterDictionary,
                spaced.Text,
                run.Provenance.Spans));
    }
}
