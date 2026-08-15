using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The Linux half of the same promise <c>WindowsClipboardRestrictionsTests</c> pins: the dictated
/// text does not outlive the paste.
///
/// The Windows tests are strict because Windows offers something strict to check - three formats,
/// all mandatory, a missing one is a leak. Linux offers nothing of the sort, and the tests below
/// say so out loud rather than dressing a best-effort marker up as a guarantee. What is written
/// down here is the shape of the answer, so that the Linux insertion adapter is built against a
/// policy and not against an assumption.
/// </summary>
public sealed class LinuxClipboardRestrictionsTests
{
    /// <summary>
    /// Nothing is mandatory, and the empty list is deliberate.
    ///
    /// A single invented "required" format would make the Linux write look as verified as the
    /// Windows one while nothing on the machine reads it.
    /// </summary>
    [Fact]
    public void NoFormatIsRequiredBecauseNoneIsHonouredEverywhere()
    {
        Assert.Empty(LinuxClipboardRestrictions.RequiredFormats);
    }

    /// <summary>
    /// The one marker worth setting, and it is exactly Klipper's.
    ///
    /// A typo here is not a failure anybody would notice: KDE would simply record the dictation
    /// as it records everything else.
    /// </summary>
    [Fact]
    public void TheKdeHintIsSpelledTheWayKlipperReadsIt()
    {
        Assert.Equal("x-kde-passwordManagerHint", LinuxClipboardRestrictions.KdePasswordManagerHint);
        Assert.Equal("secret", LinuxClipboardRestrictions.KdePasswordManagerHintSecret);
        Assert.Equal(
            [LinuxClipboardRestrictions.KdePasswordManagerHint],
            LinuxClipboardRestrictions.BestEffortFormats);
    }

    /// <summary>
    /// Best-effort is not required, and the two lists must never be confused: a build that
    /// promoted the KDE hint into <c>RequiredFormats</c> would be claiming a guarantee on GNOME.
    /// </summary>
    [Fact]
    public void BestEffortIsNotRequired()
    {
        Assert.DoesNotContain(
            LinuxClipboardRestrictions.KdePasswordManagerHint,
            LinuxClipboardRestrictions.RequiredFormats);
    }

    /// <summary>
    /// The guarantee that is actually kept: the clipboard does not hold the dictation after the
    /// paste. It is the only thing on this platform that does not depend on which manager the
    /// person installed.
    /// </summary>
    [Fact]
    public void TheClearingIsTheGuarantee()
    {
        Assert.True(LinuxClipboardRestrictions.ClearsAfterPaste);
    }

    /// <summary>
    /// The two platform policies are separate types on purpose.
    ///
    /// The Windows format names mean nothing to a Linux clipboard and would be silently ignored;
    /// borrowing one to "cover both" is how a leak gets a passing test.
    /// </summary>
    [Fact]
    public void TheWindowsFormatsAreNotBorrowed()
    {
        Assert.All(
            WindowsClipboardRestrictions.RequiredFormats,
            format =>
            {
                Assert.DoesNotContain(format, LinuxClipboardRestrictions.RequiredFormats);
                Assert.DoesNotContain(format, LinuxClipboardRestrictions.BestEffortFormats);
            });
    }
}
