namespace OpenRamble.AppCore;

/// <summary>
/// One running copy per signed-in person.
/// </summary>
/// <remarks>
/// <para>
/// Two copies would install two low-level keyboard hooks and fight over one hotkey: the first to
/// see the press starts a dictation, the second starts another, and both hold the microphone.
/// <c>Local\</c> scope, not <c>Global\</c>: two people signed into the same machine each get their
/// own dictation, their own model folder and their own dictionary.
/// </para>
/// <para>
/// <b>Why this is a type and not three lines in the composition root.</b> The obvious spelling -
/// <c>new Mutex(initiallyOwned: true, name, out bool createdNew)</c> and "not created means
/// somebody else is running" - answers a different question from the one being asked. It asks
/// whether the kernel object exists, and the object outlives the process that made it for as long
/// as any handle to it is open. A copy killed from Task Manager, an inherited handle in a child
/// process, a process still terminating around a native call that has not returned - each leaves
/// the name taken while nothing is running, and the next launch then exits without a window, an
/// icon or a single word. This asks the question that actually matters - can I take the lock -
/// and treats an abandoned lock as free, because an abandoned lock is precisely the trace of an
/// owner that died.
/// </para>
/// <para>
/// There is no macOS counterpart: <c>NSApplication</c> refuses a second copy of a bundled app by
/// itself, and the original never had to write this down.
/// </para>
/// </remarks>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary>The name used by the shipping application.</summary>
    public const string ApplicationName = @"Local\OpenRamble.SingleInstance";

    private readonly Mutex mutex;
    private readonly object gate = new();
    private bool held;
    private bool disposed;

    private SingleInstanceLock(Mutex mutex, bool held)
    {
        this.mutex = mutex;
        this.held = held;
    }

    /// <summary>
    /// Whether this process owns the lock. <c>false</c> - another copy is genuinely running and
    /// this one has nothing to do.
    /// </summary>
    public bool IsHeld
    {
        get { lock (gate) { return held; } }
    }

    /// <summary>
    /// Take the lock, or find out that a live copy already holds it.
    /// </summary>
    /// <param name="name">
    /// The lock's name. A parameter so a test can take a lock of its own instead of wrestling the
    /// shipping application for <see cref="ApplicationName"/>.
    /// </param>
    public static SingleInstanceLock Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing - killed from Task Manager, or taken down
            // with the machine. The wait still succeeded and this process now owns the lock: an
            // abandoned lock means nobody is holding it, which is the opposite of "another copy is
            // running". Reading it as "busy" is how a crash turns into an application that can
            // never be started again.
            acquired = true;
        }

        return new SingleInstanceLock(mutex, acquired);
    }

    /// <summary>
    /// Release the lock and let the handle go.
    /// </summary>
    /// <remarks>
    /// Releasing explicitly rather than relying on process exit keeps the next launch off the
    /// abandoned path: an orderly quit should look orderly to whoever comes next.
    /// </remarks>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;

            if (held)
            {
                held = false;
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Released from another thread, or never really held. Either way there is
                    // nothing left to give back and nobody to tell.
                }
            }
        }

        mutex.Dispose();
    }
}
