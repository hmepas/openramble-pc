namespace LocalASR.Tests;

/// <summary>
/// A fact that needs the real 670 MB model, and is skipped when it is not there.
/// </summary>
/// <remarks>
/// Swift writes <c>throw XCTSkip(...)</c> in the middle of the test. xUnit v2 has no runtime
/// equivalent - its execution engine ignores the dynamic-skip token that ships in the assert
/// library, and a thrown <c>SkipException</c> is reported as a failure - so the decision moves
/// to the attribute, which is the one place v2 reads a skip reason from. The result is the
/// same as Swift's: an ordinary <c>dotnet test</c> shows the test as skipped and says why,
/// and setting <c>OPENRAMBLE_MODEL_DIR</c> runs it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InstalledModelFactAttribute : FactAttribute
{
    public InstalledModelFactAttribute()
    {
        if (Directory is null)
        {
            Skip = "The model is not installed. Set OPENRAMBLE_MODEL_DIR to the engine folder "
                + "(asr-bench install puts one there).";
        }
    }

    /// <summary>The engine folder, or <c>null</c> when there is none - the same variable asr-bench reads.</summary>
    public static string? Directory
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("OPENRAMBLE_MODEL_DIR");
            return string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path) ? null : path;
        }
    }
}
