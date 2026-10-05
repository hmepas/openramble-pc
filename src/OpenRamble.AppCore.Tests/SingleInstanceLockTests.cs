namespace OpenRamble.AppCore.Tests;

/// <summary>
/// One copy runs; a copy that died leaves nothing behind.
///
/// The second half is the whole reason this type exists. A dictation app that wedges is killed
/// from Task Manager - that is what the person reaches for - and the launch right after it is the
/// one that must work. A single-instance check that reads the corpse of the previous run as "a
/// copy is already running" turns one crash into an application that shows nothing, ever again,
/// with no window and no message to say why.
/// </summary>
public sealed class SingleInstanceLockTests
{
    private static string Name() => $@"Local\OpenRamble.Test.{Guid.NewGuid():N}";

    /// <summary>
    /// Stand in for the copy that is already running.
    /// </summary>
    /// <remarks>
    /// On its own thread, and that is not decoration: a mutex is reentrant for the thread that
    /// owns it, so a second <c>Acquire</c> on the test's own thread would succeed and prove
    /// nothing about a second process.
    /// </remarks>
    private sealed class RunningCopy : IDisposable
    {
        private readonly ManualResetEventSlim quit = new();
        private readonly ManualResetEventSlim started = new();
        private readonly Thread thread;

        public RunningCopy(string name, bool abandon)
        {
            thread = new Thread(() =>
            {
                SingleInstanceLock held = SingleInstanceLock.Acquire(name);
                IsHeld = held.IsHeld;
                started.Set();
                quit.Wait();
                // An abandoning copy is one killed from Task Manager: the thread simply stops
                // existing, and nothing ever releases what it held.
                if (!abandon) { held.Dispose(); }
            })
            {
                IsBackground = true,
            };
            thread.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        }

        public bool IsHeld { get; private set; }

        public void Dispose()
        {
            quit.Set();
            thread.Join(TimeSpan.FromSeconds(10));
            quit.Dispose();
            started.Dispose();
        }
    }

    /// <summary>A genuinely running copy keeps the lock, and the newcomer knows it.</summary>
    [Fact]
    public void TestASecondCopyIsRefusedWhileTheFirstHoldsIt()
    {
        string name = Name();
        using var first = new RunningCopy(name, abandon: false);
        using SingleInstanceLock second = SingleInstanceLock.Acquire(name);

        Assert.True(first.IsHeld);
        Assert.False(second.IsHeld);
    }

    /// <summary>An orderly quit hands the name straight back.</summary>
    [Fact]
    public void TestQuittingReleasesTheNameForTheNextLaunch()
    {
        string name = Name();
        SingleInstanceLock first = SingleInstanceLock.Acquire(name);
        Assert.True(first.IsHeld);
        first.Dispose();

        using SingleInstanceLock next = SingleInstanceLock.Acquire(name);

        Assert.True(next.IsHeld);
    }

    /// <summary>
    /// An owner that died without releasing has left the lock abandoned - which means free.
    /// </summary>
    /// <remarks>
    /// A thread that exits while owning the lock is the same event the operating system reports
    /// for a process killed while owning it: the wait succeeds and raises
    /// <c>AbandonedMutexException</c> to say the previous owner never let go. Reading that as
    /// "busy" - which is what a plain <c>createdNew</c> check effectively does - is the bug.
    /// </remarks>
    [Fact]
    public void TestALockAbandonedByADeadOwnerIsFree()
    {
        string name = Name();
        var killed = new RunningCopy(name, abandon: true);
        Assert.True(killed.IsHeld);
        killed.Dispose();

        // The dead owner's thread has been joined, but on Linux the runtime marks a named mutex
        // abandoned from the thread's own exit path, which can land a beat after Join returns.
        // A real relaunch is seconds away from the crash it follows; the test only has to prove
        // the lock is free, not that it is free in the same microsecond.
        using SingleInstanceLock relaunch = AcquireWithin(name, TimeSpan.FromSeconds(2));

        Assert.True(relaunch.IsHeld);
    }

    private static SingleInstanceLock AcquireWithin(string name, TimeSpan patience)
    {
        DateTime deadline = DateTime.UtcNow + patience;
        while (true)
        {
            SingleInstanceLock attempt = SingleInstanceLock.Acquire(name);
            if (attempt.IsHeld || DateTime.UtcNow >= deadline) { return attempt; }
            attempt.Dispose();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Disposing a lock this process never held must not release somebody else's.
    /// </summary>
    [Fact]
    public void TestTheRefusedCopyGivesNothingBack()
    {
        string name = Name();
        using var first = new RunningCopy(name, abandon: false);
        SingleInstanceLock refused = SingleInstanceLock.Acquire(name);
        Assert.False(refused.IsHeld);
        refused.Dispose();

        using SingleInstanceLock third = SingleInstanceLock.Acquire(name);

        Assert.False(third.IsHeld);
    }

    /// <summary>The shipping name is per-session, never machine-wide: two people, two dictations.</summary>
    [Fact]
    public void TestTheApplicationLockIsScopedToTheSignedInPerson()
    {
        Assert.StartsWith(@"Local\", SingleInstanceLock.ApplicationName, StringComparison.Ordinal);
    }
}
