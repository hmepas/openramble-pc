using System.Diagnostics;

namespace DictationCore.Tests;

/// <summary>
/// The user fills the dictionary with his hands, and anything goes there:
/// "и т.д.", "c++", ":)", an empty right side. Replacement inside is a regular
/// expression, which means that any such character can turn from text into
/// an instruction. A damaged replacement breaks not just one word, but the entire phrase, and
/// a person finds out about this after inserting it into someone else's window.
/// </summary>
public sealed class DictionaryReplacementsEdgeCaseTests
{
    // MARK: - Special characters in what is heard

    [Fact]
    public void TestDotsInTheSpokenFormAreTakenLiterally()
    {
        // Abbreviations with dots are common in the dictionary. A dot in a regular
        // expression means "any character", and without an escaping rule
        // "т.д." would hit anything similar in length.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "т.д.", written: "так далее"),
        ];

        Assert.Equal(
            "молоко, хлеб и так далее купи",
            DictionaryReplacements.Apply(replacements, "молоко, хлеб и т.д. купи"));
        Assert.Equal(
            "тхдх не трогаем",
            DictionaryReplacements.Apply(replacements, "тхдх не трогаем"));
    }

    [Fact]
    public void TestPlusesAndBracketsInTheSpokenFormDoNotBreakTheRule()
    {
        // "c++" and ":)" are valid dictionary entries and at the same time chunks
        // of a regular expression. Unescaped parentheses make the expression
        // invalid, and the rule silently stops working entirely.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "c++", written: "C++"),
            new(spoken: ":)", written: "🙂"),
        ];

        Assert.Equal(
            "пишу на C++ и рад 🙂",
            DictionaryReplacements.Apply(replacements, "пишу на c++ и рад :)"));
    }

    [Fact]
    public void TestStarInTheSpokenFormMatchesTheStarItself()
    {
        // An asterisk in a regular expression is a repeat of the previous character.
        // In the dictionary it is just a symbol.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "*", written: "звёздочка"),
        ];

        Assert.Equal(
            "поставь звёздочка тут",
            DictionaryReplacements.Apply(replacements, "поставь * тут"));
    }

    // MARK: - Special characters in what is written

    [Fact]
    public void TestDollarInTheWrittenFormIsNotATemplateReference()
    {
        // In the replacement pattern, "$1" means "first captured group". Without
        // escaping the price "$100" would turn into "00": the user
        // would get silently corrupted text, not an error.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "цена", written: "$100"),
        ];

        Assert.Equal(
            "$100 вопроса",
            DictionaryReplacements.Apply(replacements, "цена вопроса"));
    }

    [Fact]
    public void TestBackslashInTheWrittenFormSurvives()
    {
        DictionaryReplacement[] replacements =
        [
            new(spoken: "перенос", written: @"\n"),
        ];

        Assert.Equal(
            "тут \\n строки",
            DictionaryReplacements.Apply(replacements, "тут перенос строки"));
    }

    // MARK: - Degenerate records

    [Fact]
    public void TestEmptyWrittenFormRemovesTheWordWithoutLeavingHoles()
    {
        // The empty right side is a way to remove a filler word. After the removal
        // two spaces remain in a row, and the pipeline must remove them, otherwise
        // the "cleaning" will be more noticeable than the parasite itself.
        var pipeline = new TextPipeline(replacements:
        [
            new DictionaryReplacement(spoken: "короче", written: ""),
        ]);

        Assert.Equal(
            "Нужно сделать быстро",
            pipeline.Process("короче нужно сделать короче быстро").Text);
    }

    [Fact]
    public void TestWhitespaceOnlySpokenFormIsIgnored()
    {
        // The empty left part is a dictionary line left due to inattention.
        // The "replace a space" rule would rewrite the entire text.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "   ", written: "ПРОБЕЛ"),
            new(spoken: "", written: "ПУСТО"),
        ];

        Assert.Equal(
            "обычный текст без сюрпризов",
            DictionaryReplacements.Apply(replacements, "обычный текст без сюрпризов"));
    }

    [Fact]
    public void TestSelfReplacementOnlyNormalizesTheCase()
    {
        // A word replaced by itself occurs when a person edits the
        // dictionary and changes his mind. Nothing bad should happen:
        // no looping, no doubling - just reduction to the written form.
        DictionaryReplacement[] replacements = [new(spoken: "Swift", written: "Swift")];

        Assert.Equal(
            "пишу на Swift каждый день",
            DictionaryReplacements.Apply(replacements, "пишу на swift каждый день"));
    }

    // MARK: - Word boundaries

    [Fact]
    public void TestReplacementWorksAtBothEndsOfTheText()
    {
        DictionaryReplacement[] replacements =
        [
            new(spoken: "деплой", written: "deploy"),
        ];

        Assert.Equal(
            "deploy прошёл, повторим deploy",
            DictionaryReplacements.Apply(replacements, "деплой прошёл, повторим деплой"));
    }

    [Fact]
    public void TestDigitsCountAsPartOfAWord()
    {
        // "апи2" is not "апи": otherwise the version at the end of the word would lose its meaning.
        DictionaryReplacement[] replacements = [new(spoken: "апи", written: "API")];

        Assert.Equal(
            "апи2 и API",
            DictionaryReplacements.Apply(replacements, "апи2 и апи"));
    }

    // MARK: - Order of rules

    [Fact]
    public void TestLaterRuleCanRewriteWhatAnEarlierRuleProduced()
    {
        // The rules are applied one by one to the already modified text, the long
        // ones first. This means that a short rule sees the result of a long one - and this
        // is exactly what happens when a person starts replacements that
        // intersect each other. The behavior is fixed intentionally: it must
        // be predictable rather than dependent on the order of the lines in the list.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "пул реквест", written: "PR"),
            new(spoken: "PR", written: "пиар"),
        ];

        Assert.Equal(
            "открой пиар",
            DictionaryReplacements.Apply(replacements, "открой пул реквест"));
    }

    // MARK: - Large dictionary

    [Fact]
    public void TestLargeDictionaryStaysCorrectAndFast()
    {
        // The dictionary has been growing over the years: five hundred terms is a plausible size
        // for a person who dictates every day. Replacement occurs at the moment
        // when the user is already waiting for text, so both sides are important:
        // that the desired rule worked, and that it did not take a second.
        var replacements = Enumerable.Range(0, 500)
            .Select(index => new DictionaryReplacement(
                spoken: $"термин{index}",
                written: $"term{index}"))
            .ToList();
        replacements.Add(new DictionaryReplacement(spoken: "сентри", written: "Sentry"));

        string text = "Сегодня упал сентри, посмотри логи и напиши в чат, что всё под контролем";

        var started = Stopwatch.StartNew();
        string result = DictionaryReplacements.Apply(replacements, text);
        TimeSpan elapsed = started.Elapsed;

        Assert.Contains("Sentry", result, StringComparison.Ordinal);
        Assert.DoesNotContain("сентри", result, StringComparison.Ordinal);
        // The threshold is rough: it catches not a slow machine, but an accidentally
        // introduced quadratic processing.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(3),
            $"Замена по большому словарю заняла {elapsed.TotalSeconds} с");
    }
}
