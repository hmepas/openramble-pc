namespace LocalASR;

/// <summary>
/// One native engine and the decodes running on it. Freed exactly once, by whichever comes last:
/// its retirement or its last decode.
/// </summary>
/// <remarks>
/// Freeing a native engine under a running decode would not be a nondeterministic state but a
/// crash, so an unload asked for during a decode only retires the engine, and the decode frees
/// it on its way out. The count and the flag belong to each engine rather than to its adapter:
/// one adapter-wide count with a single "retiring" slot leaked the earlier engine when a second
/// unload came during a decode, and after a decode that never returned it leaked every engine
/// unloaded from then on. Now a wedged decode pins exactly the engine it runs on.
/// </remarks>
internal sealed class RuntimeLease<T>
    where T : class, IDisposable
{
    private readonly Lock gate = new();
    private int users;
    private bool retired;

    public RuntimeLease(T value) => Value = value;

    public T Value { get; }

    /// <summary>
    /// A decode begins on this engine. Taken only while the engine is in service - under the
    /// adapter's own lock, before anything can retire it.
    /// </summary>
    public void Acquire()
    {
        lock (gate) { users++; }
    }

    /// <summary>A decode is done with this engine; the last one out of a retired engine frees it.</summary>
    public void Release()
    {
        bool free;
        lock (gate)
        {
            users--;
            free = retired && users == 0;
        }

        if (free) { Value.Dispose(); }
    }

    /// <summary>The engine is out of service: freed now if idle, else by its last decode.</summary>
    public void Retire()
    {
        bool free;
        lock (gate)
        {
            if (retired) { return; }

            retired = true;
            free = users == 0;
        }

        if (free) { Value.Dispose(); }
    }
}
