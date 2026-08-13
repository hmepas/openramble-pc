namespace DictationCore.Tests;

/// <summary>
/// Typography convolution: bring the writing to what a person would type by hand.
///
/// The stage is narrow by design. It repairs characters that can be in dictation
/// only traces of someone else's edits or invisible garbage, and does not touch anything that
/// makes sense.
/// </summary>
/// <remarks>
/// The characters the fold is about are invisible or look like their neighbours, so
/// the literals below stay escaped exactly as they are in the Swift original.
/// </remarks>
public sealed class TypographyFoldTests
{
    [Fact]
    public void TestTableHasExactlyTwelveEntries()
    {
        Assert.Equal(12, TypographyFold.Table.Count);
    }

    /// <summary>
    /// Invisible characters: part becomes a regular space, part disappears.
    ///
    /// They come from <c>written</c> in the dictionary - the person inserts the term by copy-paste
    /// along with a non-breaking space or BOM, but they are not visible in the settings at all.
    /// </summary>
    [Fact]
    public void TestInvisibleCharactersDisappear()
    {
        Assert.Equal("два слова", TypographyFold.Fold("два\u00A0слова"));
        Assert.Equal("два слова", TypographyFold.Fold("два\u202Fслова"));
        Assert.Equal("два слова", TypographyFold.Fold("два\u2009слова"));
        Assert.Equal("слово", TypographyFold.Fold("сло\u200Bво"));
        Assert.Equal("слово", TypographyFold.Fold("\uFEFFслово"));
        Assert.Equal("слово", TypographyFold.Fold("сло\u00ADво"));
    }

    [Fact]
    public void TestCurlyQuotesBecomeStraight()
    {
        Assert.Equal("'одинарные'", TypographyFold.Fold("‘одинарные’"));
        Assert.Equal("\"двойные\"", TypographyFold.Fold("“двойные”"));
    }

    /// <summary>The minus is not a hyphen, and this breaks flag recognition.</summary>
    [Fact]
    public void TestMinusSignBecomesAHyphenSoFlagsStayFlags()
    {
        string folded = TypographyFold.Fold("ключ \u2212x поставь");
        Assert.Equal("ключ -x поставь", folded);
        Assert.Equal(
            [ProtectedSpanKind.Flag],
            ProtectedSpanDetector.Detect(folded).Select(span => span.Kind).ToArray());
    }

    [Fact]
    public void TestHorizontalBarBecomesEmDash()
    {
        Assert.Equal("Москва \u2014 столица", TypographyFold.Fold("Москва \u2015 столица"));
    }

    /// <summary>
    /// Guillemets are correct Russian typography, not garbage. This is a person's text.
    ///
    /// The dash also has a meaning: it cannot be folded into a hyphen, otherwise "Moscow -
    /// the capital" will turn into "Moscow is the capital", and at the same time pairs of hyphens will appear,
    /// which the span detector will take for a flag.
    /// </summary>
    [Fact]
    public void TestGuillemetsAndDashesAreNeverFolded()
    {
        const string text = "«Москва \u2014 столица», а Питер \u2013 нет";
        Assert.Equal(text, TypographyFold.Fold(text));
    }

    /// <summary>
    /// The polisher owns the ellipsis: it has <c>…</c> in both sets of characters,
    /// and folding it into three dots would take it to the conservative dot rule.
    /// </summary>
    [Fact]
    public void TestEllipsisIsNeverFoldedBecauseThePolisherOwnsIt()
    {
        Assert.Equal("Ну… ладно", TypographyFold.Fold("Ну… ладно"));
    }

    [Fact]
    public void TestOrdinaryTextIsUntouched()
    {
        const string text = "Привет, как дела? Всё хорошо: 3.14 и т.д.";
        Assert.Equal(text, TypographyFold.Fold(text));
    }

    [Fact]
    public void TestEmptyText()
    {
        Assert.Equal("", TypographyFold.Fold(""));
    }

    // MARK: - Spans

    /// <summary>There is code inside the backticks: the quotation mark is significant and cannot be changed.</summary>
    [Fact]
    public void TestFoldSkipsProtectedSpans()
    {
        const string text = "напиши `let s = “hi”` и “цитату”";
        var spans = ProtectedSpanDetector.Detect(text);
        string folded = TypographyFold.Fold(text, spans);

        // The code is untouched.
        Assert.Contains("`let s = “hi”`", folded, StringComparison.Ordinal);
        // And the ordinary text is folded.
        Assert.Contains("\"цитату\"", folded, StringComparison.Ordinal);
    }

    /// <summary>Without spans, the fold goes through the entire text - the same path as before.</summary>
    [Fact]
    public void TestEmptySpanSetFoldsEverything()
    {
        const string text = "“раз” и “два”";
        Assert.Equal(TypographyFold.Fold(text), TypographyFold.Fold(text, []));
    }

    /// <summary>The fold does not move the span boundaries: the length of the protected pieces is preserved.</summary>
    [Fact]
    public void TestFoldKeepsSpansIntact()
    {
        const string text = "правь TextPipeline.Output\u00A0сейчас";
        var spans = ProtectedSpanDetector.Detect(text);
        string folded = TypographyFold.Fold(text, spans);
        Assert.Equal(
            spans.Select(span => span.Text),
            ProtectedSpanDetector.Detect(folded).Select(span => span.Text));
    }
}
