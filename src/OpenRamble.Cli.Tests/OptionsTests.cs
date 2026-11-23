using LocalASR;

namespace OpenRamble.Cli.Tests;

/// <summary>
/// Option parsing as upstream's <c>OpenRambleCLI/main.swift</c> does it: usage errors are
/// exit code 64 and are reported before anything is loaded.
/// </summary>
public class OptionsTests
{
    [Fact]
    public void ASingleFileDefaultsToPlainText()
    {
        Options options = Options.Parse(["talk.wav"]);

        Assert.Equal(["talk.wav"], options.Files);
        Assert.Equal(TranscriptFormat.Txt, options.Format);
        Assert.Null(options.OutputDirectory);
        Assert.Null(options.ModelDirectory);
        Assert.False(options.CheckModel);
        Assert.False(options.Help);
    }

    [Theory]
    [InlineData("txt", TranscriptFormat.Txt)]
    [InlineData("json", TranscriptFormat.Json)]
    [InlineData("srt", TranscriptFormat.Srt)]
    [InlineData("vtt", TranscriptFormat.Vtt)]
    public void EachFormatIsAccepted(string name, TranscriptFormat format) =>
        Assert.Equal(format, Options.Parse(["talk.wav", "--format", name]).Format);

    [Fact]
    public void OptionsMayComeBeforeOrAfterFiles()
    {
        Options options = Options.Parse(
            ["--format", "json", "a.wav", "--output-dir", "out", "b.wav", "--model-dir", "model", "--check-model"]);

        Assert.Equal(["a.wav", "b.wav"], options.Files);
        Assert.Equal(TranscriptFormat.Json, options.Format);
        Assert.Equal("out", options.OutputDirectory);
        Assert.Equal("model", options.ModelDirectory);
        Assert.True(options.CheckModel);
    }

    [Fact]
    public void EverythingAfterDoubleDashIsAFile()
    {
        Options options = Options.Parse(["--output-dir", "out", "--", "-recording.wav", "--help"]);

        Assert.Equal(["-recording.wav", "--help"], options.Files);
        Assert.False(options.Help);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpNeedsNoFile(string flag) => Assert.True(Options.Parse([flag]).Help);

    [Fact]
    public void CheckModelNeedsNoFile() => Assert.True(Options.Parse(["--check-model"]).CheckModel);

    [Fact]
    public void NoFileIsAUsageErrorThatShowsTheUsage()
    {
        CliError error = Assert.Throws<CliError>(() => Options.Parse([]));

        Assert.Equal(64, error.Code);
        Assert.Equal(CommandLineTool.Usage, error.Message);
    }

    [Fact]
    public void AnUnknownOptionIsAUsageError()
    {
        CliError error = Assert.Throws<CliError>(() => Options.Parse(["talk.wav", "--verbose"]));

        Assert.Equal(64, error.Code);
        Assert.Equal("Unknown option: --verbose", error.Message);
    }

    [Theory]
    [InlineData("TXT")]
    [InlineData("docx")]
    public void AnUnknownFormatIsAUsageError(string name)
    {
        CliError error = Assert.Throws<CliError>(() => Options.Parse(["talk.wav", "--format", name]));

        Assert.Equal(64, error.Code);
        Assert.Equal($"Unknown format: {name}", error.Message);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("--output-dir")]
    [InlineData("--model-dir")]
    public void AnOptionWithoutAValueIsAUsageError(string option)
    {
        CliError missing = Assert.Throws<CliError>(() => Options.Parse(["talk.wav", option]));
        CliError empty = Assert.Throws<CliError>(() => Options.Parse(["talk.wav", option, ""]));

        Assert.Equal(64, missing.Code);
        Assert.Equal($"{option} requires a value", missing.Message);
        Assert.Equal(64, empty.Code);
    }

    [Fact]
    public void SeveralFilesNeedAnOutputDirectory()
    {
        CliError error = Assert.Throws<CliError>(() => Options.Parse(["a.wav", "b.wav"]));

        Assert.Equal(64, error.Code);
        Assert.Contains("--output-dir", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, Options.Parse(["a.wav", "b.wav", "--output-dir", "out"]).Files.Count);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "talk.wav" }, true)]
    [InlineData(new[] { "--background" }, false)]
    [InlineData(new[] { "--check-model" }, true)]
    public void TheToolHandlesEverythingButTheAppsFlags(string[] arguments, bool handled) =>
        Assert.Equal(handled, CommandLineTool.Handles(arguments, "--background"));
}
