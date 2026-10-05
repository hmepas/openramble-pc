using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>One line shape, and a stage that did not run must never read as zero.</summary>
public sealed class DictationSpeedLineTests
{
    /// <summary>
    /// <c>absent</c> rather than <c>0.00</c>, because the two mean opposite things and confusing
    /// them is what hid the real cause of slow dictations for weeks.
    /// </summary>
    [Fact]
    public void TestAStageThatDidNotRunSaysSoRatherThanReadingZero()
    {
        var stages = new DictationSpeedLine.Stages(
            Total: TimeSpan.FromSeconds(8),
            Paste: null,
            CaptureFreeze: TimeSpan.FromMilliseconds(30),
            EnginePreparation: null,
            Recognition: TimeSpan.FromSeconds(7),
            EngineProcessing: TimeSpan.FromMilliseconds(200),
            EngineQueueing: null,
            AudioDecoding: null,
            RecordingReadable: null,
            AudioDuration: TimeSpan.FromSeconds(11));

        string line = DictationSpeedLine.Text(stages);

        Assert.Contains("prepare=absent", line, StringComparison.Ordinal);
        Assert.Contains("queued=absent", line, StringComparison.Ordinal);
        Assert.Contains("poolreturn=absent", line, StringComparison.Ordinal);
        Assert.Contains("mainreturn=absent", line, StringComparison.Ordinal);
        Assert.Contains("enginedispatch=absent", line, StringComparison.Ordinal);
        Assert.Contains("frame=absent", line, StringComparison.Ordinal);
        Assert.Contains("paste=absent", line, StringComparison.Ordinal);
        Assert.Contains("total=8.00s", line, StringComparison.Ordinal);
        Assert.Contains("engine=0.20s", line, StringComparison.Ordinal);
        Assert.StartsWith("dictation total=", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fast take and a streamed take are fast for different reasons, and the log has to be
    /// able to say which.
    /// </summary>
    [Fact]
    public void TestItSaysHowMuchWasRecognizedBeforeTheKeyCameUp()
    {
        static string Line(int streamed) =>
            DictationSpeedLine.Text(new DictationSpeedLine.Stages(
                Total: TimeSpan.FromSeconds(1),
                CaptureFreeze: TimeSpan.FromMilliseconds(10),
                Recognition: TimeSpan.FromMilliseconds(120),
                EngineProcessing: TimeSpan.FromMilliseconds(100),
                AudioDuration: TimeSpan.FromSeconds(90),
                StreamedSegments: streamed));

        Assert.Contains("streamed=7", Line(7), StringComparison.Ordinal);
        // Zero is an ordinary answer, not a missing measurement: a short take has nothing to
        // stream.
        Assert.Contains("streamed=0", Line(0), StringComparison.Ordinal);
    }

    [Fact]
    public void TestItNamesBothReturnSpansAndTheirExecutorWitness()
    {
        var stages = new DictationSpeedLine.Stages(
            Total: TimeSpan.FromSeconds(1),
            CaptureFreeze: TimeSpan.FromMilliseconds(20),
            Recognition: TimeSpan.FromMilliseconds(980),
            EngineProcessing: TimeSpan.FromMilliseconds(200),
            ExecutorHandover: TimeSpan.FromMilliseconds(10),
            PoolReturn: TimeSpan.FromMilliseconds(30),
            MainActorReturn: TimeSpan.FromMilliseconds(740),
            EngineDispatch: TimeSpan.FromMilliseconds(40),
            ReturnFrameWasMainThread: false,
            EngineTransport: TimeSpan.FromMilliseconds(780),
            AudioDuration: TimeSpan.FromSeconds(3));

        string line = DictationSpeedLine.Text(stages);

        Assert.Contains("poolreturn=0.03s", line, StringComparison.Ordinal);
        Assert.Contains("mainreturn=0.74s", line, StringComparison.Ordinal);
        Assert.Contains("enginedispatch=0.04s", line, StringComparison.Ordinal);
        Assert.Contains("frame=pool", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The branch is named, so a number can never again be silently about code that did not run.
    /// </summary>
    [Fact]
    public void TestItNamesTheBranchTheTakeTook()
    {
        var inMemory = new DictationSpeedLine.Stages(
            Total: TimeSpan.FromSeconds(1),
            CaptureFreeze: TimeSpan.FromMilliseconds(30),
            Recognition: TimeSpan.FromSeconds(1),
            EngineProcessing: TimeSpan.FromMilliseconds(200),
            RecordingReadable: null,
            AudioDuration: TimeSpan.FromSeconds(3));
        Assert.Contains("path=memory", DictationSpeedLine.Text(inMemory), StringComparison.Ordinal);

        DictationSpeedLine.Stages fromFile = inMemory with { RecordingReadable = TimeSpan.FromMilliseconds(40) };
        Assert.Contains("path=file", DictationSpeedLine.Text(fromFile), StringComparison.Ordinal);
    }

    /// <summary>The headline marks of a plain report map onto the line without a breakdown.</summary>
    [Fact]
    public void TestAReportWithoutABreakdownStillHasItsHeadline()
    {
        var report = new DictationSpeedReport(
            ToRecognizedText: TimeSpan.FromMilliseconds(1_234),
            ToPasteDispatched: TimeSpan.FromMilliseconds(1_500));
        string line = DictationSpeedLine.Text(DictationSpeedLine.StagesOf(report));
        Assert.Contains("total=1.23s", line, StringComparison.Ordinal);
        Assert.Contains("paste=1.50s", line, StringComparison.Ordinal);
        Assert.Contains("freeze=absent", line, StringComparison.Ordinal);
    }

    /// <summary>The decimal point does not follow the machine's locale: a log is compared across machines.</summary>
    [Fact]
    public void TestTheFormatIsInvariant()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            string line = DictationSpeedLine.Text(new DictationSpeedLine.Stages(TimeSpan.FromMilliseconds(1_250)));
            Assert.Contains("total=1.25s", line, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
