using System.Text;

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
                // Any whitespace sets it apart: after a long pause in prose the
                // paragraph mark has already become a blank line. What touches
                // the command is the last scalar before it: " ́" is one
                // character that starts with a space and ends with a mark.
                int cutoff = tailCharacters.Length - GraphemeText.Count(phrase);
                if (cutoff > 0 && !Rune.IsWhiteSpace(tailCharacters[cutoff - 1].EnumerateRunes().Last()))
                {
                    continue;
                }

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
/// | 5 | Clean Verbatim                 | deletes hesitations, stutters, spoken corrections - never rewords; spans are atomic |
/// | 6 | (snippets)                     | reserved, no stage in this wave                                              |
/// | 7 | typography-fold                | 12 characters, no spans                                                      |
/// | 8 | polisher                       | spaces, punctuation, first letter - outside of spans                         |
/// | 9 | materialize NewLine            | strictly last: the polisher trims the line break                             |
/// </code>
///
/// Stage 6 is reserved <b>as a comment, not as code</b>: a stub that does
/// nothing is a ceremony, not a contract. When it appears it stands between
/// 5 and 7, and the span gate will pick it up itself.
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
        bool phoneticMatching = true,
        bool spokenCorrections = true,
        PromptMode mode = PromptMode.Verbatim)
    {
        this.replacements = replacements ?? [];
        this.allowPressReturnCommand = allowPressReturnCommand;
        this.phoneticMatching = phoneticMatching;
        this.spokenCorrections = spokenCorrections;
        this.mode = mode;
    }

    private readonly IReadOnlyList<DictionaryReplacement> replacements;
    private readonly bool allowPressReturnCommand;

    /// <summary>
    /// Phonetic selection switch - exists for the purpose of measuring "is it needed
    /// at all on top of the acoustic prompt" (WAI_EVAL_PIPELINE=exact).
    /// </summary>
    private readonly bool phoneticMatching;

    /// <summary>
    /// "Scratch that" removes a sentence when dictating; a meeting transcript
    /// keeps what was said. See <see cref="CleanVerbatim"/>.
    /// </summary>
    private readonly bool spokenCorrections;

    /// <summary>
    /// Where the text is going. Prose apps get paragraph breaks; verbatim apps
    /// (terminals, editors, anything unknown) never receive a newline they did
    /// not ask for. See <see cref="PromptModePolicy"/>.
    /// </summary>
    private readonly PromptMode mode;

    /// <summary>
    /// Whether <paramref name="text"/> closes with a full stop, question mark, exclamation or
    /// ellipsis - what makes a trailing command its own sentence.
    /// </summary>
    internal static bool EndsSentence(string text)
    {
        string[] characters = GraphemeText.Split(text.Trim());
        return characters.Length != 0 && CleanVerbatim.SentenceEnding.Contains(characters[^1]);
    }

    /// <summary>Stages 2-8: everything between the command parser and the newline.</summary>
    private (string AfterDictionary, string Polished) Transform(string text)
    {
        // Stage 2: exact pass - before spans exist.
        string exact = DictionaryReplacements.ApplyExact(replacements, text);
        // Stage 3: the first calculation of spans is based on the result of exact replacements.
        IReadOnlyList<ProtectedSpan> spansAfterExact = ProtectedSpanDetector.Detect(exact);
        // Stage 4: phonetic selection outside of spans.
        string afterDictionary = phoneticMatching
            ? DictionaryReplacements.ApplyPhonetic(replacements, exact, spansAfterExact)
            : exact;

        // Stage 5: Clean Verbatim - only deletes, so it cannot write a word
        // that was not said. `afterDictionary` stays the checkpoint before it.
        string cleaned = CleanVerbatim.Clean(
            afterDictionary,
            spokenCorrections: spokenCorrections,
            protectedWords: CleanVerbatim.ProtectedWords(replacements),
            mode: mode);

        // Stage 7: collapsing typography outside of spans.
        string folded = TypographyFold.Fold(
            cleaned,
            ProtectedSpanDetector.Detect(cleaned));
        // Stage 8: polisher (recalculates spans itself).
        return (afterDictionary, TranscriptPolisher.Polish(folded));
    }

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
        // Stage 0: a paragraph mark (`ParagraphBreaks`) is a blank line where
        // prose is going and a plain space everywhere else.
        recognized = recognized.Replace(
            ParagraphBreaks.Marker.ToString(),
            mode == PromptMode.Prose ? "\n\n" : " ",
            StringComparison.Ordinal);
        TrailingCommandParser.Result parsed = TrailingCommandParser.Parse(recognized);
        // Return is pressed only where the caller allows it (terminals) and
        // only for a command said as its own final sentence: "…first. Send it."
        // One false trigger submits irreversibly, so "…and then we send it"
        // mid-thought stays words.
        bool refusedOnlyAsMidSentence = false;
        if (parsed.Command == TrailingCommand.PressReturn
            && (!allowPressReturnCommand || !EndsSentence(parsed.Text)))
        {
            refusedOnlyAsMidSentence = allowPressReturnCommand;
            parsed = new TrailingCommandParser.Result(text: recognized, command: null);
        }

        (string AfterDictionary, string Polished) stages = Transform(parsed.Text);
        // A hesitation or stutter right before "Send it." hides it from the
        // parser above ("…first. Um, send it.") and is gone once the text is
        // clean, so Return is decided once more on the cleaned text, by the
        // same rules: allowed here, and "Send it." as its own final sentence.
        // Only when "Send it." was the last thing said: words after it that
        // cleanup removed ("Stop, scratch that.") were taking the send back,
        // and a dictionary entry that writes "send it" was never the command.
        // The sentence end has to be said as well, so it is checked again on
        // what was said, cleaned the same way but without the dictionary: a
        // full stop an entry writes ("Acme Inc.") is not one.
        if (refusedOnlyAsMidSentence && parsed.Command is null)
        {
            TrailingCommandParser.Result said = TrailingCommandParser.Parse(
                CleanVerbatim.Clean(recognized, spokenCorrections: spokenCorrections, mode: mode));
            TrailingCommandParser.Result cleaned = TrailingCommandParser.Parse(stages.Polished);
            if (said.Command == TrailingCommand.PressReturn && EndsSentence(said.Text)
                && cleaned.Command == TrailingCommand.PressReturn && EndsSentence(cleaned.Text))
            {
                parsed = cleaned;
                stages.Polished = cleaned.Text.Trim();
            }
        }

        // A command with nothing left to act on is just words, as when it is
        // spoken alone - never an empty insert that reads as "nothing was
        // recognized" with the command silently gone. Before a Return, nothing
        // but hesitation is nothing too: "Um. Send it." never sends "Um.".
        if (parsed.Command is { } command
            && (stages.Polished.Length == 0
                || (command == TrailingCommand.PressReturn && CleanVerbatim.IsOnlyHesitation(stages.Polished))))
        {
            parsed = new TrailingCommandParser.Result(text: recognized, command: null);
            stages = Transform(recognized);
        }

        string afterDictionary = stages.AfterDictionary;
        string polished = stages.Polished;

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
