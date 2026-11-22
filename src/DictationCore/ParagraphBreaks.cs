using System.Text;

namespace DictationCore;

/// <summary>
/// Where a long pause after a sentence becomes a paragraph.
///
/// The model reports word timings with the silence folded in: a pause after a
/// sentence either stretches the last word ("green." lasting two seconds) or
/// leaves a gap before the next one. So the pause after a word is estimated as
/// the time from its start to the next word's start, minus how long the word
/// itself takes to say. Calibrated on the shipping model with two voices and
/// silences from 300 ms to 3 s: an estimate of one second matches about a
/// second and a half of real silence, which reads as a new paragraph, while
/// the ordinary pause between sentences stays well under it.
///
/// Only a word that ends a sentence can end a paragraph. The text is marked
/// with U+2029 PARAGRAPH SEPARATOR, which the text pipeline turns into a blank
/// line in prose apps and a plain space everywhere else - the policy does not
/// need to know where the words are going.
///
/// When the words cannot be matched to the text one for one, nothing is
/// guessed: the text goes on unbroken.
/// </summary>
/// <remarks>
/// The calibration was made against upstream's Parakeet build; this port runs the same model
/// family, but its timings have not been re-measured here.
/// </remarks>
public static class ParagraphBreaks
{
    public const char Marker = '\u2029';

    /// <summary>Estimated silence beyond the word itself that starts a new paragraph.</summary>
    internal static readonly TimeSpan MinimumPause = TimeSpan.FromSeconds(1.0);

    public static string Mark(string text, IReadOnlyList<ASRResult.Word> words)
    {
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1 || tokens.Length != words.Count) { return text; }
        for (int index = 0; index < tokens.Length; index += 1)
        {
            if (tokens[index] != words[index].Text) { return text; }
        }

        var result = new StringBuilder(tokens[0]);
        bool changed = false;
        for (int index = 1; index < tokens.Length; index += 1)
        {
            ASRResult.Word previous = words[index - 1];
            TimeSpan pause = words[index].Start - previous.Start - SpokenLength(previous.Text);
            if (EndsSentence(tokens[index - 1]) && pause >= MinimumPause)
            {
                result.Append(Marker);
                changed = true;
            }
            else
            {
                result.Append(' ');
            }

            result.Append(tokens[index]);
        }

        return changed ? result.ToString() : text;
    }

    /// <summary>
    /// Text already shaped for one app, made fit for the app it reaches
    /// instead (a paste of the last take goes to whatever is in front). A
    /// verbatim app never receives a paragraph break it did not ask for: each
    /// becomes the space the pipeline would have put there. A line break the
    /// person asked for with "New line." stays.
    /// </summary>
    public static string Fit(string text, PromptMode mode) =>
        mode == PromptMode.Prose ? text : text.Replace("\n\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// How long a word takes to say, generously: a quarter second plus 70 ms
    /// a letter.
    /// </summary>
    internal static TimeSpan SpokenLength(string word) =>
        TimeSpan.FromSeconds(0.25 + (0.07 * GraphemeText.Split(word).Count(IsLetter)));

    private static bool IsLetter(string character) =>
        Rune.TryGetRuneAt(character, 0, out Rune first) && Rune.IsLetter(first);

    private static bool EndsSentence(string token)
    {
        string[] characters = GraphemeText.Split(token);
        return characters.Length != 0 && CleanVerbatim.SentenceEnding.Contains(characters[^1]);
    }
}
