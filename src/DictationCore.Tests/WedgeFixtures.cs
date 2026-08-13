namespace DictationCore.Tests;

/// <summary>Helpers shared by the tests that drive a wedged engine.</summary>
internal static class WedgeFixtures
{
    /// <summary>
    /// Suspend forever and never resume - even under cancellation.
    ///
    /// The closest honest model of an inference call stuck on a dead system
    /// service: deaf to cancellation, holding no thread. The tempting
    /// alternative - a loop around a cancellable delay - models the same wedge
    /// but turns into a hot spin the moment the watchdog cancels it: the delay
    /// completes immediately on a cancelled token, the exception is swallowed,
    /// and the loop burns a thread-pool thread forever. On a developer machine
    /// with many cores nobody notices; on a small CI runner the leaked spinners
    /// starve the pool and hang every test after them.
    /// </summary>
    internal static Task<T> SuspendForever<T>() =>
        new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
}

/// <summary>
/// Every test class that drives a <see cref="DictationController"/> joins this
/// collection.
/// </summary>
/// <remarks>
/// The controller tests are the only timing-sensitive ones in the suite: they
/// hand work to background tasks and wait for it in fixed millisecond steps,
/// exactly as the Swift originals do. xUnit runs collections in parallel by
/// default, which on a small runner turns those steps into a lottery - so this
/// one collection runs alone. Everything else in the assembly keeps its
/// parallelism.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DictationControllerCollection
{
    public const string Name = "DictationController";
}
