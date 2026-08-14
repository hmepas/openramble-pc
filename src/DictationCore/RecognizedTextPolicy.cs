using System.Text;

namespace DictationCore;

/// <summary>
/// Whether a finished transcript is worth putting into somebody's document.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type has no macOS counterpart, and the engine is the reason.</b> The original asks
/// one question of the pipeline's output - is it empty - because its engine answers silence with
/// an empty string, which its own end-to-end test pins ("the model invented a phrase out of
/// silence" is the failure message there). The ONNX Parakeet this port runs answers silence and
/// room noise with punctuation instead: a comma, an ellipsis, a run of dozens of them. That text
/// is not empty, so the emptiness gate let it through and the hallucination arrived at the
/// cursor - between two dictated phrases, in the person's document.
/// </para>
/// <para>
/// So the question the original asks of its engine is asked here of the text: does anything in
/// it come from a human voice. Letters and digits are what a person dictates; punctuation is
/// what surrounds them. Nothing else is filtered - a transcript with a single letter or a single
/// digit in it ("3", "ok") is a real dictation and goes in untouched, punctuation and all.
/// </para>
/// </remarks>
public static class RecognizedTextPolicy
{
    /// <summary>
    /// Does the text carry a single letter or digit - anything a person could have said?
    /// </summary>
    /// <remarks>
    /// Counted in Unicode scalar values (<see cref="Rune"/>), never in UTF-16 code units: a
    /// letter outside the Basic Multilingual Plane arrives as a surrogate pair, and neither half
    /// of it is a letter on its own. Grapheme clusters are the wrong unit here for the opposite
    /// reason - a cluster is a letter exactly when a scalar inside it is, so splitting finer
    /// costs nothing and misses nothing.
    /// </remarks>
    public static bool CarriesWords(string text)
    {
        foreach (Rune scalar in text.EnumerateRunes())
        {
            if (Rune.IsLetter(scalar) || Rune.IsDigit(scalar)) { return true; }
        }

        return false;
    }
}
