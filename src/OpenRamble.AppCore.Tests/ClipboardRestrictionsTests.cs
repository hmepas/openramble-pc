using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The policy essence of <c>HostOnlyPasteboardTests</c>: what must never enter clipboard history.
///
/// The macOS file drives a real <c>NSPasteboard</c> and asserts on the marker types it carries.
/// The Win32 half of that - registering the formats and pushing the bytes - is untestable on
/// this host, so what is pinned here is the decision it must obey. It is the same promise: the
/// dictated text - passwords, numbers, whatever the person said - has no business surviving in
/// a store that outlives both the dictation and the application.
/// </summary>
public sealed class ClipboardRestrictionsTests
{
    /// <summary>
    /// The clipboard-history opt-out. Without it Win+V keeps the last 25 entries, dictation
    /// included.
    /// </summary>
    [Fact]
    public void Scenario001()
    {
        Assert.Contains(ClipboardRestrictions.CanIncludeInClipboardHistory, ClipboardRestrictions.RequiredFormats);
    }

    /// <summary>
    /// The cloud opt-out - the direct counterpart of macOS's <c>.currentHostOnly</c>. Without it
    /// the dictation appears on every machine the person is signed into.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        Assert.Contains(ClipboardRestrictions.CanUploadToCloudClipboard, ClipboardRestrictions.RequiredFormats);
    }

    /// <summary>
    /// The monitor opt-out - the counterpart of the nspasteboard.org markers. Third-party
    /// clipboard managers honour it; without it the dictation settles in somebody else's database
    /// forever.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        Assert.Contains(
            ClipboardRestrictions.ExcludeFromMonitorProcessing,
            ClipboardRestrictions.RequiredFormats);
    }

    /// <summary>All three, every time. A write missing any of them is a leak, not a nuisance.</summary>
    [Fact]
    public void Scenario004()
    {
        Assert.Equal(3, ClipboardRestrictions.RequiredFormats.Count);
        Assert.True(ClipboardRestrictions.IsRestricted(ClipboardRestrictions.RequiredFormats));

        foreach (string omitted in ClipboardRestrictions.RequiredFormats)
        {
            IEnumerable<string> incomplete = ClipboardRestrictions.RequiredFormats
                .Where(format => format != omitted);
            Assert.False(
                ClipboardRestrictions.IsRestricted(incomplete),
                $"a write without {omitted} must not be allowed to reach the clipboard");
        }
    }

    /// <summary>
    /// The two opt-outs are read as a DWORD, and the value that means "no" is zero. A present but
    /// non-zero value would look like a working restriction and be none.
    /// </summary>
    [Fact]
    public void Scenario005()
    {
        Assert.Equal(
            new byte[] { 0, 0, 0, 0 },
            ClipboardRestrictions.PayloadFor(ClipboardRestrictions.CanIncludeInClipboardHistory));
        Assert.Equal(
            new byte[] { 0, 0, 0, 0 },
            ClipboardRestrictions.PayloadFor(ClipboardRestrictions.CanUploadToCloudClipboard));
        // The exclusion marker's presence is the whole message.
        Assert.Empty(ClipboardRestrictions.PayloadFor(ClipboardRestrictions.ExcludeFromMonitorProcessing));
    }

    /// <summary>An unknown format has no payload to invent.</summary>
    [Fact]
    public void Scenario006()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardRestrictions.PayloadFor("CF_UNICODETEXT"));
    }

    /// <summary>The names are the registered format names; a typo is a silent leak.</summary>
    [Fact]
    public void Scenario007()
    {
        Assert.Equal(
            "ExcludeClipboardContentFromMonitorProcessing",
            ClipboardRestrictions.ExcludeFromMonitorProcessing);
        Assert.Equal("CanIncludeInClipboardHistory", ClipboardRestrictions.CanIncludeInClipboardHistory);
        Assert.Equal("CanUploadToCloudClipboard", ClipboardRestrictions.CanUploadToCloudClipboard);
    }
}
