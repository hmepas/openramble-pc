namespace DictationCore;

/// <summary>
/// Command spoken at the end of the dictation.
///
/// "Send" at the end of the phrase is not text, but an action: the user expects that
/// the message will go away, and not that the word will appear in the input field.
/// </summary>
public enum TrailingCommand
{
    PressReturn,
    NewLine,
}

/// <remarks>
/// The Swift original is a computed property and <c>CaseIterable</c> on the enum; C# enums
/// carry no members, so both live next to it. The order of <see cref="AllCases"/> is the
/// order of the cases in the Swift declaration and decides which command wins when the tail
/// matches two of them.
/// </remarks>
internal static class TrailingCommandExtensions
{
    internal static readonly TrailingCommand[] AllCases =
        [TrailingCommand.PressReturn, TrailingCommand.NewLine];

    /// <summary>Pronunciation options in Russian and English.</summary>
    internal static IReadOnlyList<string> Phrases(this TrailingCommand command) =>
        command switch
        {
            TrailingCommand.PressReturn =>
                ["отправь", "отправить", "энтер", "send it", "press enter"],
            TrailingCommand.NewLine => ["новая строка", "с новой строки", "new line"],
            _ => [],
        };
}

/// <summary>Parsing the final command.</summary>
public static class TrailingCommandParser
{
    public sealed record Result
    {
        public Result(string text, TrailingCommand? command)
        {
            Text = text;
            Command = command;
        }

        public string Text { get; }

        public TrailingCommand? Command { get; }
    }

    /// <summary>
    /// Separate the command from the text.
    ///
    /// Only the very tail of the phrase is parsed: the word "send" in the middle of a
    /// sentence is an ordinary word, not a command.
    /// </summary>
    public static Result Parse(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) { return new Result(text: trimmed, command: null); }

        // We compare the tail without the final punctuation: "...send." - the same command.
        string strippedTail = trimmed.ToLowerInvariant().Trim(TailPunctuation);
        string[] tailCharacters = GraphemeText.Split(strippedTail);

        foreach (TrailingCommand command in TrailingCommandExtensions.AllCases)
        {
            foreach (string phrase in command.Phrases())
            {
                if (!strippedTail.EndsWith(phrase, StringComparison.Ordinal)) { continue; }

                // The command must be a separate word, not the tail of another.
                int cutoff = tailCharacters.Length - GraphemeText.Count(phrase);
                if (cutoff > 0 && tailCharacters[cutoff - 1] != " ") { continue; }

                string withoutCommand = StripSuffix(phrase, trimmed).Trim(CommandSeparators);

                // A command spoken alone is just a word.
                //
                // There is no text left to insert, and pressing Return in someone else's window
                // on a single guess is impossible: the sent message is not recalled.
                // Previously, such a dictation would go nowhere - empty text
                // canceled both the insertion and the click itself.
                if (withoutCommand.Length == 0) { return new Result(text: trimmed, command: null); }

                return new Result(text: withoutCommand, command: command);
            }
        }

        return new Result(text: trimmed, command: null);
    }

    private static readonly char[] TailPunctuation = ['.', '!', '?', ',', ';', ':', ' '];

    private static readonly char[] CommandSeparators = [' ', ',', ';', ':'];

    /// <summary>
    /// Cut the phrase off the tail of the text.
    /// </summary>
    /// <remarks>
    /// We search in the string itself, and not in its copy reduced to lower case.
    /// The cast has its own length: the Turkish "İ" turns into two characters, and
    /// positions found in the copy cut the original in the wrong place - up to a crash
    /// when going beyond the line boundaries. The search counts grapheme clusters
    /// (Swift's <c>Character</c>), like the rest of the pipeline.
    /// </remarks>
    private static string StripSuffix(string phrase, string text)
    {
        string[] characters = GraphemeText.Split(text);
        string[] needle = GraphemeText.Split(phrase);
        // The port of Swift's `range(of:options: [.backwards, .caseInsensitive])`.
        for (int start = characters.Length - needle.Length; start >= 0; start -= 1)
        {
            bool matches = true;
            for (int offset = 0; offset < needle.Length; offset += 1)
            {
                if (!string.Equals(
                    characters[start + offset],
                    needle[offset],
                    StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches) { return string.Concat(characters[..start]); }
        }

        return text;
    }
}

/// <summary>
/// The full path from the recognized text to what the user sees.
///
/// <b>Table of stages.</b> The order is fixed and checked by golden tests for
/// each border (<c>TextPipelineContractTests</c>):
///
/// <code>
/// | # | Stage                          | What it does                                                                 |
/// |---|--------------------------------|------------------------------------------------------------------------------|
/// | 1 | command parser                 | separates the final command - before all replacements, otherwise the dictionary will touch it |
/// | 2 | dictionary: exact pass         | literal dictionary entries, with cases                                       |
/// | 3 | span detection                 | first calculation of protected pieces                                        |
/// | 4 | dictionary: phonetic selection | guesses - only outside of spans                                              |
/// | 5 | (language model cleaning)      | reserved, no stage in this wave                                              |
/// | 6 | (snippets)                     | reserved, no stage in this wave                                              |
/// | 7 | typography-fold                | 12 characters, no spans                                                      |
/// | 8 | polisher                       | spaces, punctuation, first letter - outside of spans                         |
/// | 9 | materialize NewLine            | strictly last: the polisher trims the line break                             |
/// </code>
///
/// Stages 5 and 6 are reserved <b>as a comment, not as code</b>: a stub that
/// does nothing is a ceremony, not a contract. When they appear they stand
/// between 4 and 7, and the span gate will pick them up itself.
///
/// The key to the entire protection model: <b>exact replacement comes before spans exist
/// at all</b>. This is the rule "the clear will of a person is higher than the protection of a
/// span" - there is no need to write a separate precedence rule, order is the rule.
/// </summary>
public sealed record TextPipeline : ITextProcessing
{
    public sealed record Output
    {
        public Output(string text, TrailingCommand? command)
        {
            Text = text;
            Command = command;
        }

        /// <summary>Text ready to be inserted.</summary>
        public string Text { get; }

        /// <summary>
        /// What to press after inserting, if the user asked for it.
        ///
        /// The line break does not appear here: it is already in the text. There is nothing
        /// to press - Return in someone else's window sends a message rather than breaking
        /// the line.
        /// </summary>
        public TrailingCommand? Command { get; }
    }

    /// <summary>The result of the run along with the origin of the text.</summary>
    /// <remarks>
    /// Swift calls this nested type <c>Run</c>; in C# a nested type and a method share one
    /// declaration space, and the method that produces it is the one every call site spells.
    /// The type took the suffix, the method kept the name.
    /// </remarks>
    public sealed record RunResult
    {
        public RunResult(Output output, PipelineProvenance provenance)
        {
            Output = output;
            Provenance = provenance;
        }

        public Output Output { get; }

        public PipelineProvenance Provenance { get; }
    }

    public TextPipeline(
        IReadOnlyList<DictionaryReplacement>? replacements = null,
        bool allowPressReturnCommand = false,
        bool phoneticMatching = true)
    {
        this.replacements = replacements ?? [];
        this.allowPressReturnCommand = allowPressReturnCommand;
        this.phoneticMatching = phoneticMatching;
    }

    private readonly IReadOnlyList<DictionaryReplacement> replacements;
    private readonly bool allowPressReturnCommand;

    /// <summary>
    /// Phonetic selection switch - exists for the purpose of measuring "is it needed
    /// at all on top of the acoustic prompt" (WAI_EVAL_PIPELINE=exact).
    /// </summary>
    private readonly bool phoneticMatching;

    public Output Process(string recognized) => Run(recognized).Output;

    /// <summary>
    /// The same as <see cref="Process"/>, but with a record of the origin of the text.
    ///
    /// Origin is built <b>unconditionally</b> - it does not depend on any
    /// user toggle. The path is the only one: <see cref="Process"/>
    /// calls this method and throws away the entry, so "branches without provenance"
    /// do not exist in the product.
    /// </summary>
    public RunResult Run(string recognized)
    {
        TrailingCommandParser.Result parsed = TrailingCommandParser.Parse(recognized);
        // In safe beta, the Send/Return voice command is disabled: one false
        // trigger irreversibly submits the message or form. The parser itself
        // remains separately testable for a possible explicit mode later.
        if (parsed.Command == TrailingCommand.PressReturn && !allowPressReturnCommand)
        {
            parsed = new TrailingCommandParser.Result(text: recognized, command: null);
        }

        // Stage 2: exact pass - before spans exist.
        string exact = DictionaryReplacements.ApplyExact(replacements, parsed.Text);
        // Stage 3: the first calculation of spans is based on the result of exact replacements.
        IReadOnlyList<ProtectedSpan> spansAfterExact = ProtectedSpanDetector.Detect(exact);
        // Stage 4: phonetic selection outside of spans.
        string afterDictionary = phoneticMatching
            ? DictionaryReplacements.ApplyPhonetic(replacements, exact, spansAfterExact)
            : exact;

        // Stage 7: collapsing typography outside of spans.
        string folded = TypographyFold.Fold(
            afterDictionary,
            ProtectedSpanDetector.Detect(afterDictionary));
        // Stage 8: polisher (recalculates spans itself).
        string polished = TranscriptPolisher.Polish(folded);

        // Stage 9: materialization of the "new line" - strictly the last one, otherwise
        // the polisher will cut off the break with its trim.
        Output output;
        if (parsed.Command == TrailingCommand.NewLine && polished.Length != 0)
        {
            // "New line" is a command that cannot be executed by pressing: Return
            // would send a message. Therefore, the break goes directly into the text - it is
            // inserted along with it. Previously, the words were cut out of the text, and in
            // return nothing happened: the entire command disappeared.
            output = new Output(text: polished + "\n", command: null);
        }
        else
        {
            output = new Output(text: polished, command: parsed.Command);
        }

        return new RunResult(
            output: output,
            provenance: new PipelineProvenance(
                afterDictionary: afterDictionary,
                finalText: output.Text,
                spans: ProtectedSpanDetector.Detect(output.Text)));
    }
}

/// <summary>
/// Anything that can turn recognized text into text to insert.
///
/// Exists so the application can choose its implementation while this package keeps its own.
/// That distinction matters more than it looks: upstream's conformance fixtures are recordings
/// of the pipeline above, and a package that delegated to another implementation would be
/// recording the thing the fixtures are meant to check. The two have to stay genuinely
/// independent for the comparison to mean anything.
/// </summary>
public interface ITextProcessing
{
    TextPipeline.RunResult Run(string recognized);
}
