using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// "Copy verbatim": what the person said, before the dictionary and before the cosmetics.
/// </summary>
/// <remarks>
/// The macOS <c>CopyRawTests</c> drive a whole app harness through a full dictation to reach one
/// boolean. The boolean is the policy; the harness belongs to the wave that brings the app state.
/// </remarks>
public sealed class CopyRawPolicyTests
{
    [Fact]
    public void Scenario001()
    {
        Assert.False(CopyRawPolicy.CanCopyRaw(null, null));
    }

    /// <summary>
    /// The main thing: the verbatim text is what the person said, not what came out after the
    /// dictionary and the polisher.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        Assert.True(CopyRawPolicy.CanCopyRaw("open poust girls", "Open Postgres"));
    }

    /// <summary>Nothing was recognized - there is nothing to copy, and the menu item stays away.</summary>
    [Fact]
    public void Scenario003()
    {
        Assert.False(CopyRawPolicy.CanCopyRaw("   ", "Anything"));
        Assert.False(CopyRawPolicy.CanCopyRaw(string.Empty, "Anything"));
    }

    /// <summary>
    /// When the cosmetics and the dictionary changed nothing, the item would be a duplicate of
    /// the last recent dictation - it must not appear.
    /// </summary>
    [Fact]
    public void Scenario005()
    {
        Assert.False(CopyRawPolicy.CanCopyRaw("Postgres", "Postgres"));
    }
}
