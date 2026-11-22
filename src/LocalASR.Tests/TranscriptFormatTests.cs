using System.Text;
using System.Text.Json;
using DictationCore;

namespace LocalASR.Tests;

public sealed class TranscriptFormatTests : IDisposable
{
    private readonly string folder;

    private static readonly FileTranscript Transcript = new(
        "Привет, world!",
        [
            new ASRResult.Word("Привет,", TimeSpan.FromSeconds(59.9), TimeSpan.FromSeconds(60.1)),
            new ASRResult.Word("world!", TimeSpan.FromSeconds(60.2), TimeSpan.FromSeconds(60.5)),
        ],
        TimeSpan.FromSeconds(61),
        TimeSpan.FromSeconds(0.1));

    public TranscriptFormatTests()
    {
        folder = Path.Combine(Path.GetTempPath(), $"format-{Guid.NewGuid():D}");
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Text(TranscriptFormat format, FileTranscript transcript) =>
        Encoding.UTF8.GetString(format.Render(transcript));

    [Fact]
    public void SubtitleTimeCarriesAcrossMinutesAndPreservesUnicode()
    {
        Assert.Equal("1\n00:00:59,900 --> 00:01:00,500\nПривет, world!\n\n", Text(TranscriptFormat.Srt, Transcript));
        Assert.Equal("WEBVTT\n\n00:00:59.900 --> 00:01:00.500\nПривет, world!\n\n", Text(TranscriptFormat.Vtt, Transcript));
    }

    [Fact]
    public void JSONKeepsOriginalFileWordTimes()
    {
        using JsonDocument json = JsonDocument.Parse(TranscriptFormat.Json.Render(Transcript));
        Assert.Equal(Transcript.Text, json.RootElement.GetProperty("text").GetString());
        JsonElement words = json.RootElement.GetProperty("words");
        Assert.Equal(60.2, words[1].GetProperty("start").GetDouble());
        Assert.Equal("world!", words[1].GetProperty("text").GetString());
    }

    /// <summary>
    /// Port addition: the keys, their order and the values Foundation writes - schema version 1,
    /// whole seconds without a decimal point, text as is, a newline at the end.
    /// </summary>
    [Fact]
    public void JSONHasTheSortedKeysAndPlainTextOfTheOriginal()
    {
        string text = Text(TranscriptFormat.Json, Transcript);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"duration\": 61,", text);
        Assert.Contains("\"schemaVersion\": 1,", text);
        Assert.Contains("\"text\": \"Привет, world!\"", text);
        Assert.True(text.IndexOf("\"duration\"", StringComparison.Ordinal) < text.IndexOf("\"words\"", StringComparison.Ordinal));
        Assert.Contains("\"end\": 60.1,", text);
        Assert.DoesNotContain("confidence", text);
    }

    [Fact]
    public void TextIsTheTranscriptAndANewline()
    {
        Assert.Equal("Привет, world!\n", Text(TranscriptFormat.Txt, Transcript));
    }

    [Fact]
    public void ExistingOutputIsPreservedAndNoTemporaryFileRemains()
    {
        string output = Path.Combine(folder, "text.txt");
        File.WriteAllText(output, "original");
        Assert.ThrowsAny<IOException>(() => TranscriptFormat.Txt.Write(Transcript, output));
        Assert.Equal("original", File.ReadAllText(output));
        Assert.Equal(["text.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        string fresh = Path.Combine(folder, "fresh.json");
        TranscriptFormat.Json.Write(Transcript, fresh);
        Assert.Equal(TranscriptFormat.Json.Render(Transcript), File.ReadAllBytes(fresh));
        if (!OperatingSystem.IsWindows())
        {
            // "Only you can read what it writes."
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fresh));
        }
    }

    [Fact]
    public void SilenceHasNoInventedSubtitleCue()
    {
        var silence = new FileTranscript("", [], TimeSpan.FromSeconds(10), TimeSpan.Zero);
        Assert.Empty(TranscriptFormat.Srt.Render(silence));
        Assert.Equal("WEBVTT\n\n", Text(TranscriptFormat.Vtt, silence));
    }

    [Fact]
    public void MillisecondQuantizationNeverRunsPastTheSourceEnd()
    {
        var tail = new FileTranscript(
            "конец",
            [new ASRResult.Word("конец", TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(1.0009))],
            TimeSpan.FromSeconds(1.0009),
            TimeSpan.Zero);
        Assert.Contains("--> 00:00:01,000", Text(TranscriptFormat.Srt, tail));
    }

    [Fact]
    public void FormatNamesAreTheCommandLineSpellings()
    {
        Assert.Equal(TranscriptFormat.Srt, TranscriptFormats.Parse("srt"));
        Assert.Null(TranscriptFormats.Parse("SRT"));
        Assert.Null(TranscriptFormats.Parse("text"));
        Assert.Equal(["txt", "json", "srt", "vtt"], Enum.GetValues<TranscriptFormat>().Select(format => format.Name));
    }
}
