namespace DictationCore.Tests;

public sealed class DictionaryReplacementsTests
{
    [Fact]
    public void TestReplacesWholeWordsOnly()
    {
        DictionaryReplacement[] replacements = [new(spoken: "код", written: "code")];

        // "кодировка" must not become "codeировка".
        Assert.Equal(
            "code и кодировка",
            DictionaryReplacements.Apply(replacements, "код и кодировка"));
    }

    [Fact]
    public void TestIsCaseInsensitiveOnInput()
    {
        // Recognition does not guarantee the case, and the result must be stable.
        DictionaryReplacement[] replacements = [new(spoken: "сентри", written: "Sentry")];

        Assert.Equal(
            "Sentry упал",
            DictionaryReplacements.Apply(replacements, "Сентри упал"));
    }

    [Fact]
    public void TestLongerPhrasesWinOverShorterOnes()
    {
        DictionaryReplacement[] replacements =
        [
            new(spoken: "пул", written: "pull"),
            new(spoken: "пул реквест", written: "pull request"),
        ];

        Assert.Equal(
            "закинул pull request",
            DictionaryReplacements.Apply(replacements, "закинул пул реквест"));
    }

    [Fact]
    public void TestHandlesRussianEnglishMix()
    {
        // Main product scenario.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "деплой", written: "deploy"),
            new(spoken: "ревью", written: "review"),
        ];

        Assert.Equal(
            "сделай review и deploy",
            DictionaryReplacements.Apply(replacements, "сделай ревью и деплой"));
    }

    [Fact]
    public void TestEmptyDictionaryLeavesTextIntact()
    {
        Assert.Equal("текст", DictionaryReplacements.Apply([], "текст"));
    }
}

public sealed class TrailingCommandParserTests
{
    [Fact]
    public void TestDetectsSendCommandAtTheEnd()
    {
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("привет как дела отправь");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("привет как дела", result.Text);
    }

    [Fact]
    public void TestIgnoresCommandWordInTheMiddle()
    {
        // "отправь" in the middle is an ordinary word, not a command.
        TrailingCommandParser.Result result =
            TrailingCommandParser.Parse("отправь ему письмо завтра");

        Assert.Null(result.Command);
        Assert.Equal("отправь ему письмо завтра", result.Text);
    }

    [Fact]
    public void TestToleratesTrailingPunctuation()
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("готово, отправь.");

        Assert.Equal(TrailingCommand.PressReturn, result.Command);
        Assert.Equal("готово", result.Text);
    }

    [Fact]
    public void TestTextWithoutCommandStaysWhole()
    {
        TrailingCommandParser.Result result = TrailingCommandParser.Parse("просто текст");

        Assert.Null(result.Command);
        Assert.Equal("просто текст", result.Text);
    }
}

public sealed class TextPipelineTests
{
    [Fact]
    public void TestFullPathFromRecognizedToInsertable()
    {
        var pipeline = new TextPipeline(
            replacements: [new DictionaryReplacement(spoken: "сентри", written: "Sentry")],
            allowPressReturnCommand: true);

        TextPipeline.Output output = pipeline.Process("сентри снова упал , посмотри. отправь");

        Assert.Equal("Sentry снова упал, посмотри.", output.Text);
        Assert.Equal(TrailingCommand.PressReturn, output.Command);
    }

    [Fact]
    public void TestCommandIsStrippedBeforeDictionaryRuns()
    {
        // The order matters: if you apply the dictionary first, it may affect
        // the command word and the command will no longer be recognized.
        var pipeline = new TextPipeline(
            replacements: [new DictionaryReplacement(spoken: "отправь", written: "ОТПРАВЬ")],
            allowPressReturnCommand: true);

        TextPipeline.Output output = pipeline.Process("готово. отправь");

        Assert.Equal(TrailingCommand.PressReturn, output.Command);
        Assert.Equal("Готово.", output.Text);
    }

    /// <summary>
    /// Return submits irreversibly, so the command counts only as its own
    /// final sentence.
    /// </summary>
    [Fact]
    public void TestSendItMidThoughtStaysWords()
    {
        var pipeline = new TextPipeline(allowPressReturnCommand: true);
        Assert.Null(pipeline.Process("and then we send it").Command);
        Assert.Null(pipeline.Process("Review the doc, send it.").Command);
        Assert.Equal("And then we send it", pipeline.Process("and then we send it").Text);

        TextPipeline.Output standalone = pipeline.Process("Ship it. Send it.");
        Assert.Equal(TrailingCommand.PressReturn, standalone.Command);
        Assert.Equal("Ship it.", standalone.Text);
    }

    [Fact]
    public void TestEmptyRecognitionProducesEmptyOutput()
    {
        TextPipeline.Output output = new TextPipeline().Process("   ");

        Assert.Equal("", output.Text);
        Assert.Null(output.Command);
    }

    [Fact]
    public void TestSafeBetaDoesNotExecuteSpokenSendCommand()
    {
        TextPipeline.Output output = new TextPipeline().Process("важное сообщение отправь");

        Assert.Equal("Важное сообщение отправь", output.Text);
        Assert.Null(output.Command);
    }

    [Fact]
    public void TestNewLineCommandActuallyBreaksTheLine()
    {
        // "New line" at the end of a phrase is the only command that cannot be executed
        // by pressing: Return in someone else's window will send a message, not break the
        // line. Therefore the line break goes directly into the text. Previously, the words
        // were cut out of the text and nothing happened in return.
        TextPipeline.Output output = new TextPipeline().Process("запушил в гитхаб новая строка");

        Assert.Equal("Запушил в гитхаб\n", output.Text);
        Assert.True(output.Command is null, "Нажимать тут нечего — перенос уже в тексте");
    }

    [Fact]
    public void TestNewLineWithoutTextInsertsNothing()
    {
        // One command without text is just a word, and there is nothing to break.
        TextPipeline.Output output = new TextPipeline().Process("новая строка");

        Assert.Equal("Новая строка", output.Text);
        Assert.Null(output.Command);
    }
}
