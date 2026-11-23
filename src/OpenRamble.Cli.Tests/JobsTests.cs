using LocalASR;

namespace OpenRamble.Cli.Tests;

/// <summary>
/// Output naming: every input keeps its own transcript, as upstream's
/// <c>scripts/tests/test-cli-transcription.py</c> checks with the same file passed twice.
/// </summary>
public class JobsTests
{
    [Fact]
    public void WithoutAnOutputDirectoryTranscriptsGoToStandardOutput()
    {
        CommandLineTool.Job job = Assert.Single(CommandLineTool.Jobs(["talk.wav"], null, TranscriptFormat.Txt));

        Assert.Equal(new CommandLineTool.Job("talk.wav", null), job);
    }

    [Fact]
    public void TheTranscriptIsNamedAfterTheInputWithTheFormatsExtension()
    {
        List<CommandLineTool.Job> jobs = CommandLineTool.Jobs(
            [Path.Combine("in", "spoken fixture.wav"), Path.Combine("in", "archive.tar.m4a")], "out", TranscriptFormat.Json);

        Assert.Equal(
            [Path.Combine("out", "spoken fixture.json"), Path.Combine("out", "archive.tar.json")],
            jobs.Select(job => job.Output));
    }

    [Fact]
    public void TheSameFileTwiceGetsANumberedSecondTranscript()
    {
        List<CommandLineTool.Job> jobs = CommandLineTool.Jobs(["a.wav", "broken.wav", "a.wav"], "out", TranscriptFormat.Json);

        Assert.Equal(
            ["a.json", "broken.json", "a-2.json"],
            jobs.Select(job => Path.GetFileName(job.Output)));
    }

    [Fact]
    public void SameNamesInDifferentFoldersOrFormatsDoNotCollide()
    {
        List<CommandLineTool.Job> jobs = CommandLineTool.Jobs(
            [Path.Combine("x", "talk.wav"), Path.Combine("y", "talk.mp3"), Path.Combine("z", "talk.m4a")], "out", TranscriptFormat.Srt);

        Assert.Equal(["talk.srt", "talk-2.srt", "talk-3.srt"], jobs.Select(job => Path.GetFileName(job.Output)));
    }

    [Fact]
    public void NamesThatDifferOnlyInCaseOrCompositionDoNotCollide()
    {
        // "é" precomposed and as "e" plus a combining accent: one name to macOS and Windows.
        List<CommandLineTool.Job> jobs = CommandLineTool.Jobs(
            ["Talk.wav", "talk.wav", "café.wav", "café.wav"], "out", TranscriptFormat.Txt);

        Assert.Equal(
            ["Talk.txt", "talk-2.txt", "café.txt", "café-2.txt"],
            jobs.Select(job => Path.GetFileName(job.Output)));
    }

    [Fact]
    public void ANumberedNameDoesNotTakeAnotherInputsName()
    {
        List<CommandLineTool.Job> jobs = CommandLineTool.Jobs(["a.wav", "a.wav", "a-2.wav"], "out", TranscriptFormat.Txt);

        Assert.Equal(["a.txt", "a-2.txt", "a-2-2.txt"], jobs.Select(job => Path.GetFileName(job.Output)));
    }

    [Fact]
    public void AHiddenFileKeepsItsWholeNameAsTheStem()
    {
        // Foundation's URL sees no extension in ".recording", and neither may the port: an
        // empty stem would write a hidden ".txt" that every such input then collides on.
        List<CommandLineTool.Job> jobs = CommandLineTool.Jobs([".recording", ".memo.wav"], "out", TranscriptFormat.Txt);

        Assert.Equal([".recording.txt", ".memo.txt"], jobs.Select(job => Path.GetFileName(job.Output)));
    }
}
