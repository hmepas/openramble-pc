namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The suites that install a diagnostic log and read it back.
/// </summary>
/// <remarks>
/// The installed log is one process-wide slot. Two classes installing their own at once - xUnit
/// runs classes in parallel - would each read a log the other had replaced, and the test that
/// lost the race would find its lines missing.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DiagnosticLogCollection
{
    public const string Name = "DiagnosticLog";
}
