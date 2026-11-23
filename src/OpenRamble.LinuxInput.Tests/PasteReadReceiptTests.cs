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
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PasteReadReceiptTests : IDisposable
{
    /// <summary>Past <c>InsertionPathPolicy.SynthesizedKeystrokeLimit</c>: the clipboard path.</summary>
    private static readonly string Long = new('x', InsertionPathPolicy.SynthesizedKeystrokeLimit + 1);

    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(60);

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Stands in for <c>$XDG_RUNTIME_DIR</c>: ours, and 0700.</summary>
    private readonly string runtime = Path.Combine(Path.GetTempPath(), $"openramble-receipts-{Guid.NewGuid():N}");

    /// <summary>Where the shim and the receipts go under it.</summary>
    private readonly string directory;

    public PasteReadReceiptTests()
    {
        Directory.CreateDirectory(runtime, Private);
        directory = new PasteReadReceipts(runtime).Directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(runtime)) { Directory.Delete(runtime, recursive: true); }
    }

    private LinuxTextInserter Inserter(
        FakeWaylandDesktop desktop,
        TimeSpan? readLimit = null,
        Action<TextInsertionError>? reportRestoreFailure = null) =>
        new(
            desktop,
            restoreDelay: TimeSpan.FromMilliseconds(60),
            reportRestoreFailure: reportRestoreFailure,
            readReceipts: new PasteReadReceipts(runtime),
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
        var receipts = new PasteReadReceipts(runtime);
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
        var receipts = new PasteReadReceipts(runtime);
        Receipt receipt = receipts.Open()!;
        string text = Path.Combine(directory, "offer");
        File.WriteAllText(text, "late");
        receipt.Dispose();

        Assert.Equal("late", RunCat(receipt.Environment, text));
        Assert.False(File.Exists(receipt.Path));
    }

    /// <summary>
    /// Reads stop being seen after they were found to work - wl-clipboard upgraded, the shim gone.
    /// A paste the target did read must not then be called unread: our own read finds no mark
    /// either, so nothing is claimed, and later pastes take the fixed-delay restore.
    /// </summary>
    [Fact]
    public async Task ReadsThatStopBeingSeenAreNotCalledUnread()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop();
        LinuxTextInserter inserter = Inserter(desktop, readLimit: TimeSpan.FromMilliseconds(300));
        TargetApplication? target = inserter.FrontmostApplication();
        Assert.NotNull((await inserter.InsertReportingMarks(Long, target)).FirstReadAt);

        desktop.ServesThroughCat = false;
        desktop.SomebodyCopies("theirs");
        InsertionMarks marks = await inserter.InsertReportingMarks(Long, target);

        Assert.Null(marks.FirstReadAt);
        Assert.Equal("theirs", desktop.ClipboardText);
        Assert.Contains(diagnostics.Lines, line => line.Contains("insert.read-receipts") && line.Contains("lost=true"));

        // The fixed-delay restore from now on: no receipt goes to wl-copy.
        await inserter.Insert(Long, target);
        Assert.Null(desktop.Runs.Last(run => run.Executable == WaylandClipboard.Copy && run.Input == Long)
            .Environment?.GetValueOrDefault(PasteReadReceipts.ReceiptVariable));
    }

    /// <summary>
    /// A second insertion while the first still waits for its read does not start: the clipboard
    /// and the read wait are the first one's until it is over.
    /// </summary>
    [Fact]
    public async Task InsertionsDoNotOverlap()
    {
        var desktop = new FakeWaylandDesktop { TargetReadsPaste = false };
        LinuxTextInserter inserter = Inserter(desktop, readLimit: TimeSpan.FromSeconds(30));
        TargetApplication? target = inserter.FrontmostApplication();

        Task first = inserter.Insert(Long, target);
        Assert.True(Until.True(() => desktop.Pastes == 1));
        Task second = inserter.Insert(Long, target);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, desktop.Pastes);

        // The press ends the first paste's wait, not the second's.
        inserter.EndTheReadWait();
        await Assert.ThrowsAsync<TextInsertionError.PasteNotRead>(() => first);
        Assert.True(Until.True(() => desktop.Pastes == 2));
        inserter.EndTheReadWait();
        await Assert.ThrowsAsync<TextInsertionError.PasteNotRead>(() => second);
    }

    /// <summary>No runtime directory, or a relative one: no receipts at all, never a shared folder.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run/user/1000")]
    public void WithoutAPrivateRuntimeDirectoryThereAreNoReceipts(string? value) =>
        Assert.Null(PasteReadReceipts.RuntimeDirectoryFrom(value));

    /// <summary>A runtime directory others can enter is no place for a <c>cat</c> on our PATH.</summary>
    [Fact]
    public void ARuntimeDirectoryOthersCanEnterGetsNoShim()
    {
        File.SetUnixFileMode(runtime, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Assert.Null(new PasteReadReceipts(runtime).Open());
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>An existing folder that is open to others, or a link to elsewhere, is not used.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShimFolderThatIsNotOursAloneIsNotUsed(bool link)
    {
        Directory.CreateDirectory(Path.Combine(runtime, "openramble"), Private);
        if (link)
        {
            string elsewhere = Path.Combine(runtime, "elsewhere");
            Directory.CreateDirectory(elsewhere, Private);
            File.CreateSymbolicLink(directory, elsewhere);
        }
        else
        {
            Directory.CreateDirectory(directory, Private | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(directory, Private | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        Assert.Null(new PasteReadReceipts(runtime).Open());
        Assert.False(File.Exists(Path.Combine(link ? Path.Combine(runtime, "elsewhere") : directory, "cat")));
    }

    /// <summary>A <c>cat</c> that is a link is replaced, never written through.</summary>
    [Fact]
    public void AShimLinkIsReplacedNotWrittenThrough()
    {
        Directory.CreateDirectory(Path.Combine(runtime, "openramble"), Private);
        Directory.CreateDirectory(directory, Private);
        string victim = Path.Combine(runtime, "victim");
        File.WriteAllText(victim, "untouched");
        File.CreateSymbolicLink(Path.Combine(directory, "cat"), victim);

        using Receipt? receipt = new PasteReadReceipts(runtime).Open();

        Assert.NotNull(receipt);
        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.Null(new FileInfo(Path.Combine(directory, "cat")).LinkTarget);
        Assert.Empty(Directory.GetFiles(directory, ".cat-*"));
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
