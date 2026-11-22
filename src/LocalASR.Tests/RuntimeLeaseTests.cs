namespace LocalASR.Tests;

/// <summary>
/// A retired engine is freed by its own last decode, whatever happens to any other engine: a
/// second unload during a decode must not leak the first, and a decode that never returns must
/// pin only the engine it runs on.
/// </summary>
public sealed class RuntimeLeaseTests
{
    private sealed class Handle : IDisposable
    {
        private int disposals;

        public int Disposals => Volatile.Read(ref disposals);

        public void Dispose() => Interlocked.Increment(ref disposals);
    }

    [Fact]
    public void AnIdleRuntimeIsFreedWhenRetired()
    {
        var handle = new Handle();
        var lease = new RuntimeLease<Handle>(handle);

        lease.Retire();

        Assert.Equal(1, handle.Disposals);
    }

    [Fact]
    public void ARuntimeInUseIsFreedByItsLastUserOnly()
    {
        var handle = new Handle();
        var lease = new RuntimeLease<Handle>(handle);
        lease.Acquire();
        lease.Acquire();

        lease.Retire();
        Assert.Equal(0, handle.Disposals);
        lease.Release();
        Assert.Equal(0, handle.Disposals);
        lease.Release();

        Assert.Equal(1, handle.Disposals);
    }

    [Fact]
    public void ASecondRetirementDuringADecodeDoesNotLeakTheFirst()
    {
        var first = new Handle();
        var second = new Handle();
        var firstLease = new RuntimeLease<Handle>(first);
        var secondLease = new RuntimeLease<Handle>(second);

        firstLease.Acquire();
        firstLease.Retire();
        secondLease.Acquire();
        secondLease.Retire();

        secondLease.Release();
        Assert.Equal(1, second.Disposals);
        firstLease.Release();

        Assert.Equal(1, first.Disposals);
    }

    [Fact]
    public void AWedgedDecodePinsOnlyItsOwnRuntime()
    {
        var wedged = new Handle();
        var wedgedLease = new RuntimeLease<Handle>(wedged);
        wedgedLease.Acquire();
        wedgedLease.Retire();

        // Every engine loaded after the wedge is used and unloaded normally.
        for (int load = 0; load < 3; load++)
        {
            var next = new Handle();
            var nextLease = new RuntimeLease<Handle>(next);
            nextLease.Acquire();
            nextLease.Release();
            nextLease.Retire();
            Assert.Equal(1, next.Disposals);
        }

        Assert.Equal(0, wedged.Disposals);
    }

    [Fact]
    public void RetiringTwiceFreesOnce()
    {
        var handle = new Handle();
        var lease = new RuntimeLease<Handle>(handle);

        lease.Retire();
        lease.Retire();

        Assert.Equal(1, handle.Disposals);
    }
}
