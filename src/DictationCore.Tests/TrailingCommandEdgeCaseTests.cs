namespace DictationCore.Tests;

/// <summary>
/// Parsing the final command is the most dangerous part of the pipeline.
///
/// An error in one direction eats a word from the text, in the other it presses Return
/// in someone else's window. The press cannot be recalled: the message will go away and the
/// form will be submitted. Therefore, the command should be recognized only where the person
/// intended it.
/// </summary>
public sealed class TrailingCommandEdgeCaseTests
{
    // MARK: - Command inside a word

    /// <summary>
    /// "Переотправь" and "отправьте" are ordinary words. If you consider them a command,
    /// the word will disappear from the text, and pressing Return will go to the application.
    /// </summary>
    [Theory]
    [InlineData("письмо переотправь")]
    [InlineData("сделай отправьте")]
    [InlineData("надо переотправить")]
    public void TestWordThatMerelyEndsLikeACommandIsNotACommand(string text)
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse(text);

        Assert.Null(result.Command);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void TestCommandGluedToPreviousWordIsNotACommand()
    {
        // Without a space before the command it is part of a word, not a separate word.
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("готовоотправь");

        Assert.Null(result.Command);
        Assert.Equal("готовоотправь", result.Text);
    }

    [Fact]
    public void TestAMarkOnTheSpaceBeforeTheCommandGluesItToThatSpace()
    {
        // " ́" is one character that starts with a space; what touches the command is
        // the mark, so it is not set apart, as in the shared core, which looks at that last
        // scalar.
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("Dear John. ́new line.");

        Assert.Null(result.Command);
        Assert.Equal("Dear John. ́new line.", result.Text);
    }

    // MARK: - Register

    [Fact]
    public void TestCommandIsRecognizedRegardlessOfCase()
    {
        // The model capitalizes the phrase at the beginning and after a period, and the command
        // could be exactly there. Recognition of the command must not depend on the case,
        // but the text before it must retain its own.
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("Готово, ОТПРАВЬ");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("Готово", result.Text);
    }

    [Fact]
    public void TestTextKeepsItsOwnCaseAfterCommandIsCutOff()
    {
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("Пиши Диме в Telegram Отправь");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("Пиши Диме в Telegram", result.Text);
    }

    // MARK: - Unicode

    [Fact]
    public void TestTextWithUnusualLettersIsCutCorrectly()
    {
        // "İ" becomes two characters when lowercased, and the length of the string changes.
        // If you cut the text by the positions found in that copy, it will be cut in the
        // wrong place: the user gets a crippled word instead of their own text.
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("Летим в İstanbul отправь");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("Летим в İstanbul", result.Text);
    }

    [Fact]
    public void TestEmojiBeforeCommandSurvives()
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("готово 🎉 отправь");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("готово 🎉", result.Text);
    }

    // MARK: - Several commands in a row

    [Fact]
    public void TestOnlyTheLastCommandIsTakenOff()
    {
        // The person repeated the command or the model heard it twice. We take off exactly
        // one: the rest is text, and it is up to the user to decide what they meant,
        // not to us.
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("готово отправь отправь");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("готово отправь", result.Text);
    }

    [Fact]
    public void TestDifferentCommandsInARowLeaveOnlyTheLastOne()
    {
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("текст новая строка отправь");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("текст новая строка", result.Text);
    }

    // MARK: - Empty after the command

    /// <summary>
    /// Previously, such a dictation went nowhere: there was no text left,
    /// and inserting an empty line along with it canceled pressing Return -
    /// the person pressed a key, said a word and received nothing.
    ///
    /// One word is a word. Pressing Return in someone else's window on this guess is
    /// impossible: the sent message is not recalled.
    /// </summary>
    [Theory]
    [InlineData("отправь")]
    [InlineData("Отправь.")]
    [InlineData("новая строка")]
    public void TestDictationConsistingOnlyOfTheCommandStaysText(string text)
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse(text);

        Assert.Null(result.Command);
        Assert.NotEqual("", result.Text);
    }

    [Fact]
    public void TestCommandAfterPunctuationOnlyTextStaysText()
    {
        // After removing the command only punctuation marks remain - which means
        // there is no text, and this is the same case as the command alone.
        TrailingCommandParser.Result result = TrailingCommandParser.Parse(", отправь");

        Assert.Null(result.Command);
    }

    // MARK: - Tail of the phrase

    [Theory]
    [InlineData("всё готово, отправь.")]
    [InlineData("всё готово отправь!  ")]
    [InlineData("всё готово: отправь...")]
    public void TestTrailingPunctuationAndSpacesDoNotHideTheCommand(string text)
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse(text);

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("всё готово", result.Text);
    }

    [Fact]
    public void TestEnglishMultiWordCommandWorks()
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("looks good send it");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("looks good", result.Text);
    }

    [Fact]
    public void TestNewLineCommandIsRecognizedSeparately()
    {
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("первый пункт новая строка");

        Assert.Equal(TrailingCommand.NewLine, result.Command);
        Assert.Equal("первый пункт", result.Text);
    }

    // MARK: - Empty input

    [Fact]
    public void TestEmptyInputHasNeitherTextNorCommand()
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("   \n  ");

        Assert.Null(result.Command);
        Assert.Equal("", result.Text);
    }
}
