namespace DictationCore.Tests;

/// <summary>
/// Placeholder proving the test project is wired up and can see DictationCore.
/// Delete once real ported tests land here.
/// </summary>
public class ScaffoldSanityTests
{
    [Fact]
    public void DictationCoreAssemblyIsReferenced()
    {
        Assert.NotNull(typeof(ScaffoldMarker).Assembly);
    }
}
