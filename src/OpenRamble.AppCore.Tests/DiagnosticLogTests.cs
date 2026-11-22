namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The diagnostic log: off by default, one line per event, and never the person's words.
///
/// This is the only file in the product that writes to disk about what happened during a
/// dictation, so its rules are the ones that need checking rather than trusting. Two of them
/// carry the whole weight: nothing is written unless somebody asked for a log, and what is
/// written is what the application did, never what was said.
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class DiagnosticLogTests
{
    /// <summary>
    /// A line reads the same way on every machine: an instant, a name, then <c>key=value</c>.
    /// </summary>
    /// <remarks>
    /// The format exists to be grepped on a Windows box with nothing installed on it, so it is
    /// pinned here rather than left to whatever a formatter felt like.
    /// </remarks>
    [Fact]
    public void ALineIsAnInstantANameAndFields()
    {
        string line = DiagnosticLog.FormatLine(
            new DateTimeOffset(2026, 8, 14, 9, 12, 33, 421, TimeSpan.Zero),
            "capture.stop.end",
            [("session", 7), ("frames", 142), ("ok", true)]);

        Assert.Equal("2026-08-14T09:12:33.421Z capture.stop.end session=7 frames=142 ok=true", line);
    }

    /// <summary>The instant is UTC whatever the machine's offset is: two logs have to compare.</summary>
    [Fact]
    public void TheInstantIsAlwaysUniversal()
    {
        string line = DiagnosticLog.FormatLine(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.FromHours(3)),
            "app.start",
            []);

        Assert.StartsWith("2026-08-14T09:00:00.000Z app.start", line, StringComparison.Ordinal);
    }

    /// <summary>Durations are milliseconds, whole. "How long", not "how long exactly".</summary>
    [Fact]
    public void DurationsBecomeWholeMilliseconds()
    {
        string line = DiagnosticLog.FormatLine(
            DateTimeOffset.UnixEpoch,
            "capture.stop.end",
            [("ms", TimeSpan.FromTicks(12_346_000)), ("budget_ms", TimeSpan.FromSeconds(3))]);

        // 1234.6 ms, rounded rather than truncated: a stop that took a millisecond longer than
        // the budget must not read as one that came in under it.
        Assert.EndsWith("capture.stop.end ms=1235 budget_ms=3000", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A missing measurement says so. <c>null</c> is never zero.
    /// </summary>
    /// <remarks>
    /// The same rule the speed report keeps: an edge that could not give a mark has no right to
    /// look instant.
    /// </remarks>
    [Fact]
    public void AMissingValueIsNamedRatherThanFakedAsZero()
    {
        string line = DiagnosticLog.FormatLine(
            DateTimeOffset.UnixEpoch,
            "dictation.speed",
            [("pasted_ms", null), ("recognized_ms", TimeSpan.Zero)]);

        Assert.EndsWith("pasted_ms=none recognized_ms=0", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// One event is one line, whatever is in it.
    /// </summary>
    /// <remarks>
    /// A stack trace arrives as a dozen lines and a Win32 message can carry anything. A reader
    /// following a hang through a file being written from four threads cannot tell an interleaved
    /// half-event from a whole one, so the newlines become separators instead of line breaks.
    /// </remarks>
    [Fact]
    public void AnEventNeverSpansTwoLines()
    {
        string line = DiagnosticLog.FormatLine(
            DateTimeOffset.UnixEpoch,
            "app.unhandled",
            [("stack", "at One()\r\n   at Two()\n   at Three()")]);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("at_One() | at_Two() | at_Three()", line, StringComparison.Ordinal);
    }

    /// <summary>A value long enough to be a problem is cut, not written.</summary>
    [Fact]
    public void ARunawayValueIsTruncated()
    {
        string sanitized = DiagnosticLog.Sanitize(new string('x', 10_000));

        Assert.True(sanitized.Length < 2_100, $"a single field grew to {sanitized.Length} characters");
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    /// <summary>Nothing at all still reads as something: an empty field is not an empty line.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    public void AnEmptyValueIsNamed(string? raw) => Assert.Equal("none", DiagnosticLog.Sanitize(raw));

    /// <summary>
    /// The command line wins over the environment.
    /// </summary>
    /// <remarks>
    /// A person who typed <c>--diag</c> into a shortcut is answering the question more recently
    /// than whoever set the variable, most likely in a different session entirely.
    /// </remarks>
    [Fact]
    public void TheCommandLineOutranksTheEnvironment()
    {
        Assert.Equal(
            @"C:\logs\typed.txt",
            DiagnosticLog.PathFrom(@"C:\logs\ambient.txt", ["--diag", @"C:\logs\typed.txt"]));
        Assert.Equal(
            @"C:\logs\ambient.txt",
            DiagnosticLog.PathFrom(@"C:\logs\ambient.txt", []));
    }

    /// <summary>
    /// No path, no log. A flag with nothing after it is ignored rather than guessed at.
    /// </summary>
    /// <remarks>
    /// A log written to a location nobody chose is worse than no log: it is a file about when a
    /// person spoke, in a place they will not think to delete.
    /// </remarks>
    [Fact]
    public void WithoutAPathThereIsNoLog()
    {
        Assert.Null(DiagnosticLog.PathFrom(null, []));
        Assert.Null(DiagnosticLog.PathFrom(null, ["--diag"]));
        Assert.Null(DiagnosticLog.PathFrom(null, ["--diag", "   "]));
        Assert.Null(DiagnosticLog.PathFrom(null, ["--verbose"]));
        Assert.Null(DiagnosticLog.PathFrom("   ", []));
    }

    /// <summary>Whitespace around a path is the shell's, not the person's.</summary>
    [Fact]
    public void SurroundingWhitespaceIsNotPartOfThePath() =>
        Assert.Equal("diag.txt", DiagnosticLog.PathFrom("  diag.txt  ", []));

    /// <summary>
    /// Every line is flushed as it is written.
    /// </summary>
    /// <remarks>
    /// The process this log exists to explain is one that hangs and is killed from Task Manager.
    /// A buffered tail is exactly the part that would have said why.
    /// </remarks>
    [Fact]
    public void EveryLineReachesTheSinkImmediately()
    {
        var sink = new FlushCountingWriter();
        var log = new DiagnosticLog(sink, () => DateTimeOffset.UnixEpoch);

        log.Write("app.start", ("pid", 4));
        log.Write("app.exit", ("code", 0));

        Assert.Equal(2, sink.Flushes);
        Assert.Equal(
            [
                "1970-01-01T00:00:00.000Z app.start pid=4",
                "1970-01-01T00:00:00.000Z app.exit code=0",
            ],
            sink.Lines);
    }

    /// <summary>
    /// Nothing is written until somebody asks for a log, and nothing after they stop asking.
    /// </summary>
    /// <remarks>
    /// The default is the whole privacy promise: a dictation product that keeps a record of every
    /// session by default keeps a record of when a person spoke.
    /// </remarks>
    [Fact]
    public void TheAmbientLogIsOffUntilItIsInstalled()
    {
        Assert.False(DiagnosticLog.IsEnabled);
        DiagnosticLog.Emit("never.written", ("chars", 12));

        var sink = new FlushCountingWriter();
        var log = new DiagnosticLog(sink, () => DateTimeOffset.UnixEpoch);
        using (DiagnosticLog.Install(log))
        {
            Assert.True(DiagnosticLog.IsEnabled);
            DiagnosticLog.Emit("dictation.state", ("to", "Listening"));
        }

        Assert.False(DiagnosticLog.IsEnabled);
        DiagnosticLog.Emit("also.never.written");

        // Contains, not equals: the ambient log is ambient, and any other test running beside
        // this one wrote into the same sink for as long as it was installed.
        Assert.Contains("1970-01-01T00:00:00.000Z dictation.state to=Listening", sink.Lines);
        Assert.DoesNotContain(sink.Lines, line => line.Contains("never.written", StringComparison.Ordinal));
    }

    /// <summary>
    /// An exception is one line: its type, its message, and every frame of its stack.
    /// </summary>
    /// <remarks>
    /// The message is carried because a Win32 or ONNX failure says everything useful in it, and
    /// no exception in this product is ever constructed from dictated text.
    /// </remarks>
    [Fact]
    public void AnExceptionKeepsItsStackOnOneLine()
    {
        var sink = new FlushCountingWriter();
        using (DiagnosticLog.Install(new DiagnosticLog(sink, () => DateTimeOffset.UnixEpoch)))
        {
            try
            {
                throw new InvalidOperationException("device is gone");
            }
            catch (InvalidOperationException error)
            {
                DiagnosticLog.EmitException("capture.failed", error, ("session", 3));
            }
        }

        string line = Assert.Single(
            sink.Lines,
            written => written.Contains("capture.failed", StringComparison.Ordinal));
        Assert.Contains("capture.failed session=3", line, StringComparison.Ordinal);
        Assert.Contains("error=InvalidOperationException", line, StringComparison.Ordinal);
        Assert.Contains("message=device_is_gone", line, StringComparison.Ordinal);
        Assert.Contains("stack=", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    /// <summary>
    /// A sink that fails takes the log down, never the dictation.
    /// </summary>
    /// <remarks>
    /// The disk fills, or the person deletes the file while the app holds it. Either way the
    /// session the log exists to explain has to keep running.
    /// </remarks>
    [Fact]
    public void ASinkThatFailsIsNotAnApplicationFailure()
    {
        var log = new DiagnosticLog(new BrokenWriter());

        log.Write("app.start");
        log.Write("app.exit");
    }

    private sealed class FlushCountingWriter : StringWriter
    {
        public List<string> Lines { get; } = [];

        public int Flushes { get; private set; }

        public override void WriteLine(string? value) => Lines.Add(value ?? string.Empty);

        public override void Flush()
        {
            Flushes += 1;
            base.Flush();
        }
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("the disk is full");
    }
}
