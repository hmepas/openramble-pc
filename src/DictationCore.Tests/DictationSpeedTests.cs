namespace DictationCore.Tests;

/// <summary>
/// Speed report: three marks from the key release.
/// </summary>
/// <remarks>
/// Only the test that stands entirely on wave-1 types is ported here. The rest of
/// the Swift <c>DictationSpeedTests</c> drives <c>DictationController</c> with a
/// scripted monotonic clock and is deferred to the wave that ports the controller:
/// <c>testSpeedIsMeasuredFromTheKeyRelease</c>, <c>testMarksAreOrdered</c>,
/// <c>testReportIgnoresWallClockJumps</c>, <c>testFailedInsertionProducesNoReport</c>,
/// <c>testUnmeasuredEdgesReportNilNotZero</c>.
/// </remarks>
public sealed class DictationSpeedTests
{
    /// <summary>The default implementation does not pretend to measure and inserts exactly once.</summary>
    [Fact]
    public async Task TestDefaultInsertReportingMarksCallsInsertExactlyOnce()
    {
        var plain = new PlainInserter();
        var marks = await ((ITextInserting)plain).InsertReportingMarks("привет", target: null);
        Assert.Null(marks.PasteDispatchedAt);
        Assert.Null(marks.ClipboardRestoredAt);
        Assert.Equal(1, plain.InsertCount);
    }

    private sealed class PlainInserter : ITextInserting
    {
        private int insertCount;

        public int InsertCount => Volatile.Read(ref insertCount);

        public Task Insert(string text, TargetApplication? target)
        {
            Interlocked.Increment(ref insertCount);
            return Task.CompletedTask;
        }

        public Task PressReturn() => Task.CompletedTask;

        public TargetApplication? FrontmostApplication() => null;
    }
}
