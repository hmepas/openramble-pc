using System.Diagnostics;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// A paste knows whether it landed (upstream 0.33.1), the Linux way: the reads of the offer are
/// counted by a <c>cat</c> that wl-copy runs to serve them.
/// </summary>
/// <remarks>
/// Ported from the read-receipt cases of upstream's <c>TextInserterTests</c>; the macOS ones about
/// AppKit's provider, the main-thread turn and the layout's paste key have no counterpart here.
/// </remarks>
[Collection(LinuxInputCollection.Name)]
public sealed class PasteReadReceiptTests : IDisposable
{
    /// <summary>Past <c>InsertionPathPolicy.SynthesizedKeystrokeLimit</c>: the clipboard path.</summary>
    private static readonly string Long = new('x', InsertionPathPolicy.SynthesizedKeystrokeLimit + 1);

    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(60);

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"openramble-receipts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private LinuxTextInserter Inserter(
        FakeWaylandDesktop desktop,
        TimeSpan? readLimit = null,
        Action<TextInsertionError>? reportRestoreFailure = null) =>
        new(
            desktop,
            restoreDelay: TimeSpan.FromMilliseconds(60),
            reportRestoreFailure: reportRestoreFailure,
            readReceipts: new PasteReadReceipts(directory),
            readFloor: Floor,
            readLimit: readLimit ?? TimeSpan.FromSeconds(3));

    /// <summary>
    /// The clipboard comes back as soon as the target has read the dictation, and before the
    /// insertion returns - not a fixed second later, when a slow application may still read it.
    /// </summary>
    [Fact]
    public async Task APasteThatWasReadGivesTheClipboardBackBeforeReturning()
    {
        var desktop = new FakeWaylandDesktop();
        desktop.SomebodyCopies("a link the person was about to paste");
        LinuxTextInserter inserter = Inserter(desktop);

        InsertionMarks marks = await inserter.InsertReportingMarks(Long, inserter.FrontmostApplication());

        Assert.Equal("a link the person was about to paste", desktop.ClipboardText);
        Assert.NotNull(marks.FirstReadAt);
        Assert.NotNull(marks.ClipboardRestoredAt);
        // Never sooner than the floor after Ctrl+V: a reader that is about to read still finds it.
        Assert.True(marks.PasteDispatchedAt!.Value.DurationTo(marks.ClipboardRestoredAt!.Value) >= Floor);
        // The receipt goes with the paste.
        Assert.Empty(Directory.GetFiles(directory, "receipt-*"));
    }

    /// <summary>
    /// Ctrl+V went out and nobody read the clipboard: nothing was pasted. The person's clipboard
    /// comes back, and the insertion says so instead of returning a silent success.
    /// </summary>
    [Fact]
    public async Task APasteNobodyReadIsReportedAndTheClipboardComesBack()
    {
        var desktop = new FakeWaylandDesktop { TargetReadsPaste = false };
        desktop.SomebodyCopies("theirs");
        LinuxTextInserter inserter = Inserter(desktop, readLimit: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TextInsertionError.PasteNotRead>(
            () => inserter.Insert(Long, inserter.FrontmostApplication()));

        Assert.Equal(1, desktop.Pastes);
        Assert.Equal("theirs", desktop.ClipboardText);
    }

    /// <summary>
    /// A clipboard history reads every offer the moment it appears. That read is not the paste:
    /// a paste the application in front never read is still unread.
    /// </summary>
    [Fact]
    public async Task AWatcherReadingTheOfferDoesNotMakeAPasteLand()
    {
        var desktop = new FakeWaylandDesktop { TargetReadsPaste = false, WatcherReadsEveryOffer = true };
        LinuxTextInserter inserter = Inserter(desktop, readLimit: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TextInsertionError.PasteNotRead>(
            () => inserter.Insert(Long, inserter.FrontmostApplication()));
    }

    /// <summary>
    /// A press of the dictation key while the paste waits for its read: nothing has appeared, so
    /// the person is trying again. The wait ends at the floor, not at the limit.
    /// </summary>
    [Fact]
    public async Task APressEndsTheReadWaitAtTheFloor()
    {
        var desktop = new FakeWaylandDesktop { TargetReadsPaste = false };
        LinuxTextInserter inserter = Inserter(desktop, readLimit: TimeSpan.FromSeconds(30));
        long began = Stopwatch.GetTimestamp();

        Task insertion = inserter.Insert(Long, inserter.FrontmostApplication());
        Assert.True(Until.True(() => desktop.Pastes == 1));
        inserter.EndTheReadWait();

        await Assert.ThrowsAsync<TextInsertionError.PasteNotRead>(() => insertion);
        Assert.True(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5));
    }

    /// <summary>A press with no paste under way belongs to nothing, and cuts no later paste short.</summary>
    [Fact]
    public async Task APressBetweenPastesEndsNoWait()
    {
        var desktop = new FakeWaylandDesktop();
        LinuxTextInserter inserter = Inserter(desktop);

        inserter.EndTheReadWait();
        InsertionMarks marks = await inserter.InsertReportingMarks(Long, inserter.FrontmostApplication());

        Assert.NotNull(marks.FirstReadAt);
    }

    /// <summary>
    /// The paste landed, and only putting the person's clipboard back failed. That is no failure of
    /// the insertion: it is said on the side, and the words are not called lost.
    /// </summary>
    [Fact]
    public async Task ARestoreThatFailsAfterAPasteLandedIsReportedOnTheSide()
    {
        var desktop = new FakeWaylandDesktop { ClipboardHasImage = true };
        TextInsertionError? reported = null;
        LinuxTextInserter inserter = Inserter(desktop, reportRestoreFailure: error => reported = error);

        InsertionMarks marks = await inserter.InsertReportingMarks(Long, inserter.FrontmostApplication());

        Assert.NotNull(marks.FirstReadAt);
        Assert.Null(marks.ClipboardRestoredAt);
        Assert.IsType<TextInsertionError.InsertedButClipboardRestoreFailed>(reported);
        Assert.Null(desktop.ClipboardText);
    }

    /// <summary>
    /// Nothing was pasted, and the person's clipboard could not be put back either: both are
    /// theirs to know.
    /// </summary>
    [Fact]
    public async Task AnUnreadPasteThatAlsoLostTheClipboardSaysSo()
    {
        var desktop = new FakeWaylandDesktop { ClipboardHasImage = true, TargetReadsPaste = false };
        LinuxTextInserter inserter = Inserter(desktop, readLimit: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TextInsertionError.ClipboardRestoreFailed>(
            () => inserter.Insert(Long, inserter.FrontmostApplication()));
    }

    /// <summary>
    /// A wl-clipboard that does not serve through <c>cat</c> leaves no receipt. Calling every paste
    /// unread there would be worse than not knowing: the fixed-delay restore stands, nothing is
    /// claimed, and the question is asked once.
    /// </summary>
    [Fact]
    public async Task WhereAReadCannotBeSeenTheFixedRestoreStands()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop { ServesThroughCat = false };
        desktop.SomebodyCopies("theirs");
        LinuxTextInserter inserter = Inserter(desktop);
        TargetApplication? target = inserter.FrontmostApplication();

        InsertionMarks marks = await inserter.InsertReportingMarks(Long, target);
        Assert.Null(marks.FirstReadAt);
        Assert.Equal("false", diagnostics.Field("insert.read-receipts", "seen"));
        Assert.True(Until.True(() => desktop.ClipboardText == "theirs"));

        int readsBefore = desktop.Runs.Count(run => run.Executable == WaylandClipboard.Paste);
        await inserter.Insert(Long, target);
        int readsAfter = desktop.Runs.Count(run => run.Executable == WaylandClipboard.Paste);
        // One read of the person's clipboard, none to ask again whether a read can be seen.
        Assert.Equal(1, readsAfter - readsBefore);
    }

    /// <summary>The shim itself, run for real: it notes the read and hands over to the real cat.</summary>
    [Fact]
    public void TheShimNotesTheReadAndServesTheText()
    {
        var receipts = new PasteReadReceipts(directory);
        using Receipt receipt = receipts.Open()!;
        string text = Path.Combine(directory, "offer");
        File.WriteAllText(text, "served whole");

        string served = RunCat(receipt.Environment, text);

        Assert.Equal("served whole", served);
        Assert.Equal(1, receipt.Reads);
        Assert.Equal("served whole", RunCat(receipt.Environment, text));
        Assert.Equal(2, receipt.Reads);
    }

    /// <summary>A late read of an offer already put away belongs to nobody, and leaves no file.</summary>
    [Fact]
    public void TheShimDoesNotBringBackAReceiptThatIsGone()
    {
        var receipts = new PasteReadReceipts(directory);
        Receipt receipt = receipts.Open()!;
        string text = Path.Combine(directory, "offer");
        File.WriteAllText(text, "late");
        receipt.Dispose();

        Assert.Equal("late", RunCat(receipt.Environment, text));
        Assert.False(File.Exists(receipt.Path));
    }

    /// <summary>What wl-copy does to serve a read: run <c>cat</c> from the PATH it was given.</summary>
    private static string RunCat(IReadOnlyDictionary<string, string> environment, string file)
    {
        var start = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cat \"$1\"");
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(file);
        foreach ((string name, string value) in environment) { start.Environment[name] = value; }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
