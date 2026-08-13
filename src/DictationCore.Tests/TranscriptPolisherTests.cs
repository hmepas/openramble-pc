namespace DictationCore.Tests;

/// <remarks>
/// The Swift original keeps this class in <c>TextPipelineTests.swift</c> next to the
/// pipeline suites; here it lives with the type it tests, because the pipeline is a
/// different porting cluster.
/// </remarks>
public sealed class TranscriptPolisherTests
{
    [Fact]
    public void TestRemovesSpaceBeforePunctuation()
    {
        Assert.Equal("Привет, как дела?", TranscriptPolisher.Polish("привет , как дела ?"));
    }

    [Fact]
    public void TestAddsSpaceOnlyBeforeNewSentence()
    {
        // A capital letter after a dot means a new sentence - a space is
        // really needed there.
        Assert.Equal("Готово. Пойдём дальше", TranscriptPolisher.Polish("Готово.Пойдём дальше"));
    }

    /// <summary>
    /// All of the above actually comes from the model: it itself transforms
    /// "three and fourteen hundredths" into "3.14". Previously, processing put a space
    /// after any period and collapsed numbers, versions, domains and abbreviations.
    /// </summary>
    [Theory]
    [InlineData("Testing numbers like 3.14 and dates like January 5.")]
    [InlineData("Версия 2.0.1 вышла")]
    [InlineData("Смотри на wai.computer")]
    [InlineData("Это т.д. и т.п.")]
    [InlineData("Цена 1,500 рублей")]
    public void TestDoesNotBreakNumbersVersionsDomainsAndAbbreviations(string input)
    {
        Assert.Equal(input, TranscriptPolisher.Polish(input));
    }

    [Fact]
    public void TestSeparatesWordsGluedToAComma()
    {
        // The model regularly glues the enumeration: "first, second". There is no capital
        // further on, and a space did not appear before - the text remained stuck together.
        Assert.Equal("Первое, второе", TranscriptPolisher.Polish("первое,второе"));
        Assert.Equal("Раз, два, три, четыре", TranscriptPolisher.Polish("раз, два,три,четыре"));
        Assert.Equal("Стоп! поехали", TranscriptPolisher.Polish("стоп!поехали"));
        Assert.Equal("Что? ничего", TranscriptPolisher.Polish("что?ничего"));
        Assert.Equal("Раз; два", TranscriptPolisher.Polish("раз;два"));
    }

    /// <summary>
    /// The only place where the comma is close to the next character
    /// to the point is a decimal fraction. It differs in that a number comes next.
    /// </summary>
    [Theory]
    [InlineData("Ставка 3,14 процента")]
    [InlineData("Цена 1,500 рублей")]
    [InlineData("Координаты 55,7558 и 37,6173")]
    public void TestDecimalCommaSurvivesTheSameRule(string input)
    {
        Assert.Equal(input, TranscriptPolisher.Polish(input));
    }

    /// <summary>
    /// Colon and period live inside links, times and abbreviations, so
    /// they do not add a space to a lowercase letter - the cost of an error is higher than the benefit.
    /// </summary>
    [Theory]
    [InlineData("Открой https://wai.computer/openramble/")]
    [InlineData("Встреча в 12:30")]
    [InlineData("Это т.д. и т.п.")]
    public void TestColonAndPeriodStayConservative(string input)
    {
        Assert.Equal(input, TranscriptPolisher.Polish(input));
    }

    [Fact]
    public void TestCollapsesRepeatedSpaces()
    {
        Assert.Equal(
            "Слишком много пробелов",
            TranscriptPolisher.Polish("слишком   много    пробелов"));
    }

    [Fact]
    public void TestKeepsLineBreaks()
    {
        // Line breaks are meaningful: the user could dictate a list.
        Assert.Equal(
            "Первая строка\nвторая строка",
            TranscriptPolisher.Polish("первая строка\nвторая  строка"));
    }

    [Fact]
    public void TestCapitalizesOnlyTheFirstLetter()
    {
        Assert.Equal("Привет мир", TranscriptPolisher.Polish("привет мир"));
        // We do not touch the capitalization anymore, we do not break the abbreviations.
        Assert.Equal("HTTP это протокол", TranscriptPolisher.Polish("HTTP это протокол"));
    }

    [Fact]
    public void TestDoesNotTouchNumbersWrittenAsWords()
    {
        // We do not try to expand numerals into numbers: in Russian this
        // rests on cases and breaks more than it repairs.
        Assert.Equal("Двадцать пять рублей", TranscriptPolisher.Polish("двадцать пять рублей"));
    }

    [Fact]
    public void TestEmptyInputStaysEmpty()
    {
        Assert.Equal("", TranscriptPolisher.Polish("   "));
    }
}
