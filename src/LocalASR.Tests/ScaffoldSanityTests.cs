namespace LocalASR.Tests;

/// <summary>
/// Placeholder proving the test project is wired up and can see LocalASR.
/// Delete once real ported tests land here.
/// </summary>
public class ScaffoldSanityTests
{
    [Fact]
    public void LocalAsrAssemblyIsReferenced()
    {
        Assert.NotNull(typeof(ScaffoldMarker).Assembly);
    }
}
