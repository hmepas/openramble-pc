namespace LocalASR.Tests;

/// <summary>Returning punctuation after the dictionary rescorer - without a model and without sound.</summary>
public class PunctuationReattachmentTests
{
    private static string Restore(string original, string rescored) =>
        PunctuationReattachment.Restore(original, rescored);

    private static int MarkCount(string text) =>
        text.Count(character => ".,!?:;…«»\"'()—–".Contains(character, StringComparison.Ordinal));

    private static List<string> Cores(string text) =>
        PunctuationReattachment.Fields(text).Select(field => field.Core).ToList();

    // MARK: - Main

    [Fact]
    public void EqualStringsAreReturnedUnchanged() =>
        Assert.Equal("Привет, мир.", Restore("Привет, мир.", "Привет, мир."));

    /// <summary>The very defect: replacing a word took the comma with it.</summary>
    [Fact]
    public void CommaComesBackToTheReplacedWord() =>
        Assert.Equal(
            "Данные лежат в Postgres, а кэш отдельно.",
            Restore(
                "Данные лежат в постгресе, а кэш отдельно.",
                "Данные лежат в Postgres а кэш отдельно."));

    [Fact]
    public void SentenceFinalPeriodIsNotLost() =>
        Assert.Equal("Открой Postgres.", Restore("Открой постгрес.", "Открой Postgres"));

    /// <summary>The rescorer's own mark is more important - otherwise it would become "Postgres,,".</summary>
    [Fact]
    public void MarkIsNeverDoubled() =>
        Assert.Equal(
            "Открой Postgres, потом",
            Restore("Открой постгрес, потом", "Открой Postgres, потом"));

    /// <summary>
    /// Gluing several words into a term: the mark is given only by the last of them, and
    /// punctuation does not fit inside the term.
    /// </summary>
    [Fact]
    public void MergingTwoWordsKeepsOnlyTheTrailingMark()
    {
        string result = Restore("Сделай пул реквест, потом", "Сделай pull-request потом");

        Assert.Equal("Сделай pull-request, потом", result);
        Assert.DoesNotContain("pull,", result, StringComparison.Ordinal);
    }

    /// <summary>Breaking a word into several: the mark sits at the end, not inside.</summary>
    [Fact]
    public void SplittingAWordDoesNotScatterMarksInside()
    {
        string result = Restore("Открой постгрес.", "Открой Post gres");

        Assert.DoesNotContain("Post. gres", result, StringComparison.Ordinal);
        Assert.EndsWith(".", result, StringComparison.Ordinal);
    }

    // MARK: - What you cannot touch

    /// <summary>Marks inside a word are not removed - they are removed only from the edges.</summary>
    [Fact]
    public void PunctuationInsideAWordIsNeverTouched()
    {
        string[] samples =
        [
            "Число 3.14 точное",
            "И т.д. дальше",
            "Ссылка https://wai.computer/openramble/ here",
            "I don't know",
            "Пишем тайп-скрипт сейчас",
        ];
        foreach (string text in samples)
        {
            Assert.Equal(text, Restore(text, text));
        }
    }

    /// <summary>
    /// The texts diverged too much - we return them as they are.
    ///
    /// This is not quiet degradation but today's behavior: inventing punctuation is worse
    /// than losing it.
    /// </summary>
    [Fact]
    public void TooDifferentTextsAreLeftAlone()
    {
        const string Rescored = "совершенно другой набор слов без единого совпадения";

        Assert.Equal(Rescored, Restore("Привет, как дела? Хорошо.", Rescored));
    }

    [Fact]
    public void NewlinesAndSpacingSurvive()
    {
        const string Original = "Первая строка,\nвторая строка.";
        const string Rescored = "Первая строка\nвторая строка";

        string result = Restore(Original, Rescored);

        Assert.Contains("\n", result, StringComparison.Ordinal);
        Assert.Equal("Первая строка,\nвторая строка.", result);
    }

    [Fact]
    public void EmptyStrings()
    {
        Assert.Equal(string.Empty, Restore(string.Empty, string.Empty));
        Assert.Equal(string.Empty, Restore("Привет.", string.Empty));
        Assert.Equal("Привет", Restore(string.Empty, "Привет"));
    }

    // MARK: - Invariants

    private static readonly (string Original, string Rescored)[] Corpus =
    [
        ("Данные лежат в постгресе, а кэш отдельно.", "Данные лежат в Postgres а кэш отдельно."),
        ("Открой постгрес.", "Открой Postgres"),
        ("Сделай пул реквест, потом деплой!", "Сделай pull-request потом deploy"),
        ("Число 3.14, и т.д.", "Число 3.14 и т.д."),
        ("Первая строка,\nвторая строка.", "Первая строка\nвторая строка"),
        ("Привет, как дела?", "Привет как дела"),
    ];

    /// <summary>
    /// Invariant 3, the most important: words never change.
    ///
    /// Otherwise returning punctuation would undo the dictionary edit the rescorer works for.
    /// </summary>
    [Fact]
    public void WordsAreNeverChanged()
    {
        foreach ((string original, string rescored) in Corpus)
        {
            Assert.Equal(Cores(rescored), Cores(Restore(original, rescored)));
        }
    }

    /// <summary>Invariant 1: a mark produced by the rescorer is not removed.</summary>
    [Fact]
    public void MarksProducedByTheRescorerAreNeverRemoved()
    {
        foreach ((string original, string rescored) in Corpus)
        {
            string result = Restore(original, rescored);
            Assert.True(
                MarkCount(result) >= MarkCount(rescored),
                "what the rescorer already wrote may not be lost");
        }
    }

    /// <summary>Invariant 2: no new marks appear.</summary>
    [Fact]
    public void NoMarkIsEverInvented()
    {
        foreach ((string original, string rescored) in Corpus)
        {
            string result = Restore(original, rescored);
            Assert.True(
                MarkCount(result) <= Math.Max(MarkCount(original), MarkCount(rescored)),
                "marks are taken only from the union of the two sides");
        }
    }

    /// <summary>What all this is for: the marks on a corpus stop disappearing.</summary>
    [Fact]
    public void MarkCountRecoversTowardTheOriginal()
    {
        foreach ((string original, string rescored) in Corpus)
        {
            if (MarkCount(original) <= MarkCount(rescored))
            {
                continue;
            }

            string result = Restore(original, rescored);
            Assert.True(
                MarkCount(result) > MarkCount(rescored),
                $"the restoration must bring something back: {original}");
        }
    }
}
