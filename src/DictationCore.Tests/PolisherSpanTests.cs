namespace DictationCore.Tests;

/// <summary>
/// Polisher and protected spans.
///
/// This is where the fix for the defect lives, for which the span model appeared.
/// </summary>
public sealed class PolisherSpanTests
{
    /// <summary>The same defect: the space after the dot before the capital letter was causing names to fall apart.</summary>
    [Fact]
    public void TestDoesNotSplitADottedIdentifier()
    {
        Assert.Equal("Открой TextPipeline.Output", TranscriptPolisher.Polish("открой TextPipeline.Output"));
        Assert.Equal("Зови AppState.shared", TranscriptPolisher.Polish("зови AppState.shared"));
        Assert.Equal("Правь Foo.Bar", TranscriptPolisher.Polish("правь Foo.Bar"));
    }

    /// <summary>
    /// The same defect, only the name is in quotation marks or brackets.
    ///
    /// « » is the standard Russian quotation mark, and the model puts it around
    /// names all the time. One such character used to disable the protection of
    /// the entire token, and the polisher split the name in two.
    /// </summary>
    [Fact]
    public void TestDoesNotSplitAQuotedOrBracketedIdentifier()
    {
        Assert.Equal(
            "Открой «TextPipeline.Output»",
            TranscriptPolisher.Polish("открой «TextPipeline.Output»"));
        Assert.Equal("Зови (AppState.shared)", TranscriptPolisher.Polish("зови (AppState.shared)"));
        Assert.Equal("Правь [AppState.shared]", TranscriptPolisher.Polish("правь [AppState.shared]"));
    }

    /// <summary>
    /// The actual sentence boundary is still being broken up.
    ///
    /// The rule about identifiers is only about ASCII, so the Russian text is
    /// not relevant at all.
    /// </summary>
    [Fact]
    public void TestStillSplitsARealSentenceBoundary()
    {
        Assert.Equal("Готово. Дальше поедем", TranscriptPolisher.Polish("Готово.Дальше поедем"));
        Assert.Equal("Готово. Пойдём дальше", TranscriptPolisher.Polish("Готово.Пойдём дальше"));
    }

    /// <summary>
    /// The capital guard remained in place: numbers, versions, domains and
    /// contractions do not become spans and stick to it.
    /// </summary>
    [Theory]
    [InlineData("Testing numbers like 3.14 and dates like January 5.")]
    [InlineData("Версия 2.0.1 вышла")]
    [InlineData("Смотри на wai.computer")]
    [InlineData("Это т.д. и т.п.")]
    [InlineData("Встреча в 12:30")]
    public void TestConservativeDotAndColonRulesAreUnchangedThroughSpans(string input)
    {
        Assert.Equal(input, TranscriptPolisher.Polish(input));
    }

    [Fact]
    public void TestPolisherNeverEditsInsideBackticks()
    {
        Assert.Equal("Запусти `swift  test` сейчас", TranscriptPolisher.Polish("запусти `swift  test` сейчас"));
        Assert.Equal("Напиши `a , b` вот так", TranscriptPolisher.Polish("напиши `a , b` вот так"));
    }

    [Fact]
    public void TestPathsAndFlagsSurvivePolishing()
    {
        Assert.Equal(
            "Открой /etc/hosts и добавь --dry-run",
            TranscriptPolisher.Polish("открой /etc/hosts и добавь --dry-run"));
    }

    /// <summary>
    /// The first word of the identifier is not capitalized: it is a name, not the beginning
    /// of a sentence. <c>OnTextInserted</c> is a different name.
    /// </summary>
    [Fact]
    public void TestIdentifierAtTheStartKeepsItsCase()
    {
        Assert.Equal("onTextInserted сработал", TranscriptPolisher.Polish("onTextInserted сработал"));
        // An ordinary word - as before.
        Assert.Equal("Привет", TranscriptPolisher.Polish("привет"));
    }

    /// <summary>Collapse of spaces does not go inside the span, but it removes the extra space nearby.</summary>
    [Fact]
    public void TestWhitespaceCollapseStopsAtTheSpanBoundary()
    {
        Assert.Equal("До `a  b` после", TranscriptPolisher.Polish("до   `a  b`   после"));
    }

    /// <summary>
    /// The same boundary, but the backticks are inside quotes: the quotation mark
    /// is not a reason to lose the pair and collapse the spaces the code needs.
    /// </summary>
    [Fact]
    public void TestWhitespaceCollapseStopsAtTheSpanBoundaryBehindAQuote()
    {
        Assert.Equal(
            "Напиши \"`a  b`\" вот так",
            TranscriptPolisher.Polish("напиши \"`a  b`\" вот так"));
    }
}
