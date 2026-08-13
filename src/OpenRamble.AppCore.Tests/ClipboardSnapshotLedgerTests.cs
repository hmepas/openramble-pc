using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The bookkeeping half of <c>HostOnlyPasteboardTests</c>: two dictations inside one restore
/// window, and what happens when the privacy budget expires first.
/// </summary>
public sealed class ClipboardSnapshotLedgerTests
{
    private static ClipboardSnapshot Snapshot(string marker) =>
        new(new Dictionary<uint, byte[]> { [13] = System.Text.Encoding.UTF8.GetBytes(marker) });

    private static string Marker(ClipboardSnapshot snapshot) =>
        System.Text.Encoding.UTF8.GetString(snapshot.Formats[13]);

    /// <summary>A transaction gives back exactly what it was handed.</summary>
    [Fact]
    public void Scenario001()
    {
        var ledger = new ClipboardSnapshotLedger();
        Guid id = ledger.Insert(Snapshot("what the person copied"), ownedSequence: 7);

        var claim = Assert.IsType<ClipboardClaim.Snapshot>(ledger.Take(id));
        Assert.Equal("what the person copied", Marker(claim.Contents));
        Assert.Equal(7u, claim.OwnedSequence);
    }

    /// <summary>Claiming twice is not an error, but it is not a second restore either.</summary>
    [Fact]
    public void Scenario002()
    {
        var ledger = new ClipboardSnapshotLedger();
        Guid id = ledger.Insert(Snapshot("copied"), ownedSequence: 7);

        ledger.Take(id);

        Assert.IsType<ClipboardClaim.Missing>(ledger.Take(id));
    }

    /// <summary>
    /// Two dictations inside one restore window.
    ///
    /// The restore is deferred by a second after Ctrl+V, and a second dictation within that
    /// second is an ordinary thing - two short replies in a chat. The second write lands on OUR
    /// text: a snapshot taken there would capture the dictation instead of the person's
    /// clipboard, and the person's own content would be gone for good.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        var ledger = new ClipboardSnapshotLedger();
        Guid first = ledger.Insert(Snapshot("what the person copied"), ownedSequence: 7);

        // The second write sees the clipboard still standing at the sequence the first one left.
        var inherited = Assert.IsType<ClipboardClaim.Snapshot>(ledger.Inherit(ownedSequence: 7));
        Assert.Equal("what the person copied", Marker(inherited.Contents));
        Guid second = ledger.Insert(inherited.Contents, ownedSequence: 8);

        // The deferred restore of the first dictation arrives when the second has already
        // replaced it: there is nothing of its own left to put back.
        Assert.IsType<ClipboardClaim.Missing>(ledger.Take(first));

        var final = Assert.IsType<ClipboardClaim.Snapshot>(ledger.Take(second));
        Assert.Equal("what the person copied", Marker(final.Contents));
    }

    /// <summary>A write that lands on somebody else's clipboard inherits nothing.</summary>
    [Fact]
    public void Scenario004()
    {
        var ledger = new ClipboardSnapshotLedger();
        ledger.Insert(Snapshot("copied"), ownedSequence: 7);

        Assert.IsType<ClipboardClaim.Missing>(ledger.Inherit(ownedSequence: 9));
    }

    /// <summary>
    /// The privacy budget erases the bytes on a timer. If the clipboard is still ours at that
    /// point, the person's previous contents are lost for good - and that cannot be passed over
    /// in silence, nor may the dictation be left sitting in the clipboard.
    /// </summary>
    [Fact]
    public async Task Scenario005()
    {
        var ledger = new ClipboardSnapshotLedger(TimeSpan.FromMilliseconds(50));
        Guid id = ledger.Insert(Snapshot("what the person copied"), ownedSequence: 7);

        await Task.Delay(400);

        var expired = Assert.IsType<ClipboardClaim.Expired>(ledger.Take(id));
        Assert.Equal(7u, expired.OwnedSequence);
    }

    /// <summary>
    /// Losing the bytes must not lose the fact of ownership: without it, restore would decide the
    /// clipboard was somebody else's and silently leave the dictation in it.
    /// </summary>
    [Fact]
    public async Task Scenario006()
    {
        var ledger = new ClipboardSnapshotLedger(TimeSpan.FromMilliseconds(50));
        ledger.Insert(Snapshot("what the person copied"), ownedSequence: 7);

        await Task.Delay(400);

        var inherited = Assert.IsType<ClipboardClaim.Expired>(ledger.Inherit(ownedSequence: 7));
        Assert.Equal(7u, inherited.OwnedSequence);
        // The fact travels on to the write that took the clipboard over.
        Guid second = ledger.InsertExpired(ownedSequence: 8);
        Assert.IsType<ClipboardClaim.Expired>(ledger.Take(second));
    }

    /// <summary>
    /// A restore that arrives out of order must not put the dictation back over the person's own
    /// content.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        var ledger = new ClipboardSnapshotLedger();
        Guid first = ledger.Insert(Snapshot("what the person copied"), ownedSequence: 7);
        var inherited = Assert.IsType<ClipboardClaim.Snapshot>(ledger.Inherit(ownedSequence: 7));
        Guid second = ledger.Insert(inherited.Contents, ownedSequence: 8);

        Assert.IsType<ClipboardClaim.Snapshot>(ledger.Take(second));
        Assert.IsType<ClipboardClaim.Missing>(ledger.Take(first));
    }

    /// <summary>
    /// A screenshot has no text at all - the snapshot keeps every format byte for byte and
    /// invents none. Losing a screenshot to a dictated chat reply is exactly the kind of small
    /// theft that breaks trust for good.
    /// </summary>
    [Fact]
    public void Scenario008()
    {
        byte[] dib = [1, 2, 3, 4];
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        var ledger = new ClipboardSnapshotLedger();
        Guid id = ledger.Insert(
            new ClipboardSnapshot(new Dictionary<uint, byte[]> { [8] = dib, [49_500] = png }),
            ownedSequence: 3);

        var claim = Assert.IsType<ClipboardClaim.Snapshot>(ledger.Take(id));

        Assert.Equal(dib, claim.Contents.Formats[8]);
        Assert.Equal(png, claim.Contents.Formats[49_500]);
        Assert.False(claim.Contents.Formats.ContainsKey(13), "restore must not invent a text format");
    }

    /// <summary>An empty clipboard is a legitimate thing to restore to.</summary>
    [Fact]
    public void Scenario009()
    {
        var ledger = new ClipboardSnapshotLedger();
        Guid id = ledger.Insert(ClipboardSnapshot.Empty, ownedSequence: 1);

        var claim = Assert.IsType<ClipboardClaim.Snapshot>(ledger.Take(id));
        Assert.True(claim.Contents.IsEmpty);
    }
}
