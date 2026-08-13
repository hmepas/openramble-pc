using AsrBench;

namespace LocalASR.Tests;

/// <summary>
/// The scorer is the only thing every quality number rests on. If it lies, the conclusions lie
/// too, and unnoticed: wrong normalization turns a correct recognition into an error and back.
/// </summary>
/// <remarks>
/// Ported from the Swift package's separate <c>ASRBenchTests</c> target. There is no second
/// test project here: the scorer lives in the bench tool, and this project already owns the
/// LocalASR side of the same measurement chain.
/// </remarks>
public class TranscriptScorerTests
{
    private static IReadOnlyList<string> Words(string text) => TextNormalizer.Normalize(text).Words;

    // MARK: - Numerals

    [Fact]
    public void FoldsRussianCardinalToDigits()
    {
        Assert.Equal(["128", "мегабайт"], Words("сто двадцать восемь мегабайт"));
        Assert.Equal(["250"], Words("двести пятьдесят"));
        Assert.Equal(["2400", "строк"], Words("две тысячи четыреста строк"));
        Assert.Equal(["999", "рублей"], Words("девятьсот девяносто девять рублей"));
    }

    [Fact]
    public void FoldsOrdinalAndCaseFormsToTheSameNumber()
    {
        // This is why the folding goes into numbers and not into words: Russian changes the
        // numeral's gender and case, but the number stays the same.
        foreach (string form in new[]
                 {
                     "двадцать пятое", "двадцать пятого", "двадцать пять", "двадцати пяти",
                 })
        {
            Assert.Equal(["25"], Words(form));
        }
    }

    [Fact]
    public void DigitsAndWordsCompareEqual()
    {
        ScoreReport report = TranscriptScorer.Score(
            reference: "файл весит сто двадцать восемь мегабайт",
            hypothesis: "Файл весит 128 мегабайт.");

        Assert.Equal(0, report.Words.Errors);
    }

    [Fact]
    public void OrdinalWithHyphenSuffixIsANumber()
    {
        // The model writes "10-я мысль" where "десятая мысль" was said.
        Assert.Equal(["10", "мысль"], Words("10-я мысль"));
        Assert.Equal(["10", "мысль"], Words("десятая мысль"));
    }

    [Fact]
    public void TimeIsNotSummedIntoOneNumber()
    {
        // "четырнадцать тридцать" is a time. Adding would give 44 and hide any error in the
        // hours or the minutes.
        Assert.Equal(["в", "14", "30"], Words("в четырнадцать тридцать"));
        Assert.Equal(["at", "9", "45"], Words("at nine forty five"));
    }

    [Fact]
    public void HalfBecomesFraction()
    {
        Assert.Equal(["3.5", "часа"], Words("три с половиной часа"));
        Assert.Equal(["1.5", "hours"], Words("one and a half hours"));
    }

    [Fact]
    public void DecimalSeparatorStaysInsideNumber()
    {
        Assert.Equal(["версия", "2.01"], Words("версия 2.01"));
        Assert.Equal(["занял", "3.5", "часа"], Words("занял 3,5 часа"));
    }

    [Fact]
    public void PrepositionIsNotSwallowedByNumber()
    {
        // "с" is only useful in "с половиной". In "три с утра" it is a preposition, and the
        // scorer has no right to lose it.
        Assert.Equal(["3", "с", "утра"], Words("три с утра"));
    }

    [Fact]
    public void EnglishHundredMultiplies()
    {
        Assert.Equal(["321", "files"], Words("three hundred and twenty one files"));
        Assert.Equal(["200", "milliseconds"], Words("two hundred milliseconds"));
    }

    [Fact]
    public void NonNumbersAreLeftAlone()
    {
        // "сто" and "стол" differ by one letter - guessing by the stem would turn the
        // furniture into a number.
        Assert.Equal(["стол", "и", "стойка"], Words("стол и стойка"));
        Assert.Equal(["одиночество"], Words("одиночество"));
    }

    // MARK: - Punctuation

    [Fact]
    public void PunctuationIsNotSilentlyDropped()
    {
        NormalizedText normalized = TextNormalizer.Normalize("Привет, как дела? Всё хорошо!");

        Assert.Equal([",", "?", "!"], normalized.Marks);
        Assert.DoesNotContain(normalized.Words, word => word.Contains(',', StringComparison.Ordinal));
        Assert.Contains(",", normalized.Tokens);
    }

    [Fact]
    public void MissingPunctuationCostsNothingInWordsButShowsUpSeparately()
    {
        ScoreReport report = TranscriptScorer.Score(
            reference: "привет, как дела?",
            hypothesis: "привет как дела");

        Assert.Equal(0, report.Words.Errors);
        Assert.Equal(2, report.Punctuation.Deletions);
        Assert.True(
            report.WordsWithPunctuation.Rate > report.Words.Rate,
            "the price of punctuation must be visible in its own metric");
    }

    [Fact]
    public void InnerHyphenAndApostropheStayInsideWord()
    {
        Assert.Equal(["тайп-скрипт"], Words("тайп-скрипт"));
        Assert.Equal(["don't", "stop"], Words("don't stop"));
        Assert.Equal(["—"], TextNormalizer.Normalize("да — нет").Marks);
    }

    [Fact]
    public void PercentSignIsSeparatedButIsNotPunctuation()
    {
        NormalizedText normalized = TextNormalizer.Normalize("скидка 75%");

        Assert.Equal(["скидка", "75"], normalized.Words);
        Assert.Empty(normalized.Marks);
    }

    // MARK: - Counting errors

    [Fact]
    public void CountsSubstitutionsDeletionsAndInsertions()
    {
        ScoreReport report = TranscriptScorer.Score(
            reference: "утро день вечер ночь",
            hypothesis: "утро дань вечер ночь снова");

        Assert.Equal(1, report.Words.Substitutions);
        Assert.Equal(1, report.Words.Insertions);
        Assert.Equal(0, report.Words.Deletions);
        Assert.Equal(4, report.Words.ReferenceLength);
        Assert.Equal(0.5, report.Words.Rate, 4);
    }

    [Fact]
    public void DeletionIsReportedAsLostWord()
    {
        ScoreReport report = TranscriptScorer.Score(
            reference: "нужно сохранить последнюю фразу",
            hypothesis: "нужно сохранить фразу");

        Assert.Equal(1, report.Words.Deletions);
        Assert.Equal(
            ["Missing: последнюю"],
            report.Differences.Select(difference => difference.Description).ToList());
    }

    [Fact]
    public void EmptyReferenceWithTextIsFullError()
    {
        // Silence that gave birth to text is a hundred per cent error, not zero because of a
        // division by zero.
        ScoreReport report = TranscriptScorer.Score(reference: "", hypothesis: "откуда-то взялся текст");

        Assert.Equal(1, report.Words.Rate);
        Assert.Equal(1, report.CharacterErrorRate);
    }

    [Fact]
    public void SilenceMatchedBySilenceIsNotAnError()
    {
        ScoreReport report = TranscriptScorer.Score(reference: "", hypothesis: "");

        Assert.Equal(0, report.Words.Rate);
        Assert.Equal(0, report.CharacterErrorRate);
    }

    [Fact]
    public void CaseAndYoAreNotErrors()
    {
        ScoreReport report = TranscriptScorer.Score(reference: "Всё готово", hypothesis: "все ГОТОВО");

        Assert.Equal(0, report.Words.Errors);
    }

    [Fact]
    public void CharacterErrorRateCountsLetters()
    {
        ScoreReport report = TranscriptScorer.Score(reference: "кот", hypothesis: "код");

        Assert.Equal(1, report.Words.Rate);
        Assert.Equal(1.0 / 3.0, report.CharacterErrorRate, 4);
    }

    [Fact]
    public void AlignmentAgreesWithLevenshteinDistance()
    {
        // The path and the distance are separate functions; if they part ways, WER and CER
        // part ways too.
        string[] reference = "один два три четыре пять шесть".Split(' ');
        string[] hypothesis = "один три четыре пять восемь семь".Split(' ');

        (ScoreReport.Counts counts, _) = TranscriptScorer.Align(reference, hypothesis);
        // The same sequences, but one character per word: the distance is a different
        // function, and the two have no right to part ways.
        int distance = TranscriptScorer.Distance(
            reference.Select(word => word[..1]).ToList(),
            hypothesis.Select(word => word[..1]).ToList());

        Assert.Equal(distance, counts.Errors);
    }
}
