using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DictationCore;

/// <summary>
/// Custom replacement: how it is recognized → how it should be written.
///
/// This is why a dictionary is needed: the model does not know the names that the user
/// lives every day, and consistently writes "sentry" instead of "Sentry".
/// </summary>
/// <remarks>
/// The Swift original is a <c>struct</c> with <c>var</c> fields; here it is an immutable
/// record — nothing in the port mutates a replacement in place, and <c>with</c> covers
/// the editing that the app layer does.
/// </remarks>
[JsonConverter(typeof(DictionaryReplacementConverter))]
public sealed record DictionaryReplacement
{
    public DictionaryReplacement(
        string spoken,
        string written,
        bool inflects = true,
        bool noAcousticBoost = false,
        bool allowsPhoneticMatching = true)
        : this(Guid.NewGuid(), spoken, written, inflects, noAcousticBoost, allowsPhoneticMatching)
    {
    }

    public DictionaryReplacement(
        Guid id,
        string spoken,
        string written,
        bool inflects = true,
        bool noAcousticBoost = false,
        bool allowsPhoneticMatching = true)
    {
        Id = id;
        Spoken = spoken;
        Written = written;
        Inflects = inflects;
        NoAcousticBoost = noAcousticBoost;
        AllowsPhoneticMatching = allowsPhoneticMatching;
    }

    public Guid Id { get; init; }

    /// <summary>What the model heard.</summary>
    public string Spoken { get; init; }

    /// <summary>What should appear in the text.</summary>
    public string Written { get; init; }

    /// <summary>
    /// Whether to wait for case endings for the entry.
    ///
    /// A property of a record, not an inference from its letters. "Deploy" is a word, it has
    /// "deploy" and "deployment". "Komet" is not a word, but a recognition flaw: it has
    /// no cases, but there is a "comet", which the inflected stem "comets-"
    /// ate along with the term. Measuring it was worth it: real Russian words in
    /// the kill radius of the set was 89, of which four families are ordinary speech.
    /// </summary>
    public bool Inflects { get; init; }

    /// <summary>
    /// Whether to keep the term away from the acoustic prompt.
    ///
    /// A property of a record, not a list in someone else's file. There are terms whose
    /// Cyrillic sound is a common Russian word: "deploy" catches "warm",
    /// "Sentry" - "in the center", "commit" - "comet". The library sees the similarity
    /// through alphabets (<c>sim("центр", Sentry) = 0.83</c>), and an English
    /// CTC model will always value the Latin term higher than the Cyrillic word by
    /// the same entry. Worse, the "центр" in an honest Russian phrase and the "центр" in
    /// place of Sentry are the same text, and no threshold will separate them.
    /// Such terms remain in the dictionary of replacements, whose rules ordinary speech
    /// does not touch (docs/benchmarks.md).
    ///
    /// Previously, this was a hardcoded list of three words in <c>VocabularyBoost</c>.
    /// A person's own terms have never been included in it, but add your own
    /// he couldn't at all.
    /// </summary>
    public bool NoAcousticBoost { get; init; }

    /// <summary>
    /// Whether this spelling may be used as a fuzzy phonetic candidate.
    ///
    /// Exact decoder repairs are deliberately literal. Feeding a strange merged
    /// phrase into the fuzzy matcher widens its blast radius beyond the measured
    /// output it was added for. Personal replacements keep the historical <c>true</c>
    /// default.
    /// </summary>
    public bool AllowsPhoneticMatching { get; init; }
}

/// <summary>
/// Own coding is needed because of <c>inflects</c> and <c>noAcousticBoost</c>: in dictionaries
/// recorded before their appearance there are no such keys. The absence of <c>inflects</c> is
/// "inflects", as it was; the absence of <c>noAcousticBoost</c> is "boosts",
/// because it used to be a list, not a record, that decided it.
/// </summary>
/// <remarks>
/// Written by hand rather than through attributes: <c>System.Text.Json</c> fills a missing
/// member with <c>default</c>, which for these three flags is exactly the wrong answer.
/// The key order on the way out repeats the Swift field order, and the identifier keeps
/// Foundation's uppercase <c>UUID.uuidString</c> shape so that files stay byte-identical
/// across the two platforms.
/// </remarks>
internal sealed class DictionaryReplacementConverter : JsonConverter<DictionaryReplacement>
{
    public override DictionaryReplacement Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a replacement must be an object");
        }

        Guid? id = null;
        string? spoken = null;
        string? written = null;
        bool inflects = true;
        bool noAcousticBoost = false;
        bool allowsPhoneticMatching = true;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? throw new JsonException("a key without a name");
            reader.Read();
            switch (name)
            {
                case "id":
                    id = Guid.Parse(reader.GetString() ?? throw new JsonException("id is not a string"));
                    break;
                case "spoken":
                    spoken = reader.GetString();
                    break;
                case "written":
                    written = reader.GetString();
                    break;
                case "inflects":
                    inflects = reader.GetBoolean();
                    break;
                case "noAcousticBoost":
                    noAcousticBoost = reader.GetBoolean();
                    break;
                case "allowsPhoneticMatching":
                    allowsPhoneticMatching = reader.GetBoolean();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (id is null || spoken is null || written is null)
        {
            throw new JsonException("a replacement needs id, spoken and written");
        }

        return new DictionaryReplacement(
            id.Value,
            spoken,
            written,
            inflects,
            noAcousticBoost,
            allowsPhoneticMatching);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DictionaryReplacement value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id.ToString("D").ToUpperInvariant());
        writer.WriteString("spoken", value.Spoken);
        writer.WriteString("written", value.Written);
        writer.WriteBoolean("inflects", value.Inflects);
        writer.WriteBoolean("noAcousticBoost", value.NoAcousticBoost);
        writer.WriteBoolean("allowsPhoneticMatching", value.AllowsPhoneticMatching);
        writer.WriteEndObject();
    }
}

/// <summary>Applying a dictionary to recognized text.</summary>
public static class DictionaryReplacements
{
    /// <summary>
    /// Replace all occurrences along word boundaries.
    ///
    /// Borders are required: without them, replacing "код" → "code" would turn
    /// "кодировка" into "codeировка". The input register is ignored because
    /// recognition does not guarantee its stability.
    ///
    /// There are two passes, and the order is important. First, an exact match with the cases -
    /// it is responsible for everything that is literally written in the dictionary. Then
    /// phonetic selection: it catches those spellings of the term that are not in the
    /// dictionary and cannot be, because the model writes the same word
    /// differently in different phrases. Measurement of both passes is in docs/benchmarks.md.
    /// The passes are split (<see cref="ApplyExact"/> + <see cref="ApplyPhonetic"/>) because between
    /// them is the detection of protected spans: exact replacement is the explicit will
    /// of a human and goes before spans even exist, and the phonetic
    /// addition is a guess and must bypass spans. Order is the rule of
    /// priority; there is no need to write separate precedence rules.
    /// </summary>
    public static string Apply(
        IReadOnlyList<DictionaryReplacement> replacements,
        string text,
        bool phoneticMatching = true)
    {
        string exact = ApplyExact(replacements, text);
        if (!phoneticMatching) { return exact; }
        return ApplyPhonetic(replacements, exact, []);
    }

    /// <summary>
    /// First pass: exact match with cases.
    ///
    /// The literal rise of the previous body is a mutating and general accumulator, therefore
    /// replacements can be layered (a later rule rewrites what an earlier one produced).
    /// This behavior is fixed by tests and cannot be changed.
    /// </summary>
    internal static string ApplyExact(IReadOnlyList<DictionaryReplacement> replacements, string text)
    {
        if (replacements.Count == 0 || text.Length == 0) { return text; }

        string result = text;
        // Long options first: otherwise "пул" will be replaced inside "пул реквест".
        foreach (DictionaryReplacement replacement in replacements
            .OrderByDescending(replacement => GraphemeText.Count(replacement.Spoken)))
        {
            string spoken = DictionaryWhitespace.Trimmed(replacement.Spoken);
            if (spoken.Length == 0) { continue; }
            result = ReplaceWholeWords(
                needle: spoken,
                replacement: replacement.Written,
                inflected: replacement.Inflects,
                text: result);
        }

        return result;
    }

    /// <summary>
    /// Second pass: phonetic selection outside of protected spans.
    ///
    /// An empty set of spans literally goes to the previous path, and not "to a new
    /// path with an empty filter". The matcher is greedy, its behavior is emergent, and
    /// identity should rest on construction, not on reasoning.
    /// </summary>
    internal static string ApplyPhonetic(
        IReadOnlyList<DictionaryReplacement> replacements,
        string text,
        IReadOnlyList<ProtectedSpan> spans)
    {
        if (replacements.Count == 0 || text.Length == 0) { return text; }
        if (spans.Count == 0) { return PhoneticMatching.Apply(replacements, text); }
        return PhoneticMatching.Apply(replacements, text, spans);
    }

    internal static string ReplaceWholeWords(
        string needle,
        string replacement,
        bool inflected,
        string text)
    {
        string expression = Pattern(needle, inflected);
        Regex regex;
        try
        {
            regex = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return text;
        }

        // Only "$" is special in a .NET replacement string; a backslash is literal there.
        return regex.Replace(text, replacement.Replace("$", "$$", StringComparison.Ordinal));
    }

    /// <summary>
    /// Case endings that Russian adds to a borrowed word.
    ///
    /// The list is closed and short on purpose: these are not "similar words", but exactly those
    /// tails, due to which an exact match never works.
    /// </summary>
    private static readonly string[] Endings =
    [
        "ами", "ями", "ой", "ей", "ом", "ем", "ов", "ев", "ам", "ям", "ах", "ях",
        "а", "е", "и", "ы", "у", "ю", "я",
    ];

    /// <summary>
    /// Expression for finding a replacement in the text.
    ///
    /// The Russian word is inflected, but the Latin term is not. A person says
    /// "до релиза", "в питоне", "без даунтайма" and waits for "до release",
    /// "в Python", "без downtime" - replacement by exact match here
    /// never works. Therefore, case endings are allowed at the end of
    /// Cyrillic substitutions.
    ///
    /// This does not apply to Latin substitutions: English terms in Russian speech are not
    /// inflected, and the extra tail there would mean a different word.
    /// </summary>
    internal static string Pattern(string needle, bool inflected = true)
    {
        const string leading = "(?<![\\p{L}\\p{N}])";
        const string trailing = "(?![\\p{L}\\p{N}])";

        string[] words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !inflected || !IsInflectable(needle))
        {
            return leading + Regex.Escape(needle) + trailing;
        }

        // For words ending in "й", the ending replaces the "й" itself: деплой → деплоя, деплою.
        // Therefore, we cut it off and add it back to the list of tails - otherwise
        // the word itself would cease to coincide with itself.
        string last = words[^1];
        bool hasShortI = last.EndsWith("й", StringComparison.Ordinal);
        string stem = hasShortI ? string.Concat(GraphemeText.Split(last)[..^1]) : last;
        string[] tailList = hasShortI ? ["й", .. Endings] : Endings;
        string tails = string.Join("|", tailList.Select(Regex.Escape));

        string head = string.Join("\\s+", words[..^1].Select(Regex.Escape));
        string prefix = head.Length == 0 ? "" : head + "\\s+";

        return leading + prefix + Regex.Escape(stem) + "(?:" + tails + ")?" + trailing;
    }

    /// <summary>
    /// Is it worth waiting for the case ending?
    ///
    /// Only for Cyrillic and only from four letters: in short words the tail
    /// is too often the beginning of another word.
    ///
    /// Words with a vowel ("фигма" → "фигме") do not fall here, and this is deliberate.
    /// To catch them, you would have to cut off the final vowel - and then
    /// "сентри" would become the stem "центр", and "в центре города" would become
    /// "в Sentry города". We guard exactly this case with a separate test. The price is
    /// that such a replacement is caught only in the form in which it is written; in
    /// the supplied set that ends with six entries on a vowel: "ревью",
    /// "код ревью", "кот ревью", "сентри", "центри", "линти".
    /// </summary>
    private static bool IsInflectable(string needle)
    {
        string[] words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || GraphemeText.Count(words[^1]) < 4) { return false; }
        return GraphemeText.Split(needle).All(character =>
            // The hyphen is part of the term: the model writes "тайп скрипт" in one word,
            // and only its tail is inflected anyway.
            character == " " || character == "-"
                || character.EnumerateRunes().All(rune => rune.Value is >= 0x0400 and <= 0x04FF));
    }
}

/// <summary>
/// Foundation's <c>CharacterSet.whitespaces</c>: horizontal space only, never a line break.
/// </summary>
/// <remarks>
/// .NET's <c>string.Trim()</c> is Foundation's <c>whitespacesAndNewlines</c>, which is a
/// different set — a dictionary entry ending in a line break keeps it in the original.
/// </remarks>
internal static class DictionaryWhitespace
{
    internal static string Trimmed(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && IsWhitespace(text[start])) { start += 1; }
        while (end > start && IsWhitespace(text[end - 1])) { end -= 1; }
        return text[start..end];
    }

    private static bool IsWhitespace(char character) =>
        character == '\t'
            || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;
}
