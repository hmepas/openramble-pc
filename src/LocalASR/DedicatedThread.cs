namespace LocalASR;

/// <summary>
/// Run one piece of blocking work on a thread of its own.
/// </summary>
/// <remarks>
/// <para>
/// Recognition is a synchronous native call that runs for as long as the audio needs; reading a
/// recording waits on a disk. Both used to run on the thread pool, which is shared with
/// everything else in the process - and a pool that is busy makes a dictation queue behind work
/// that has nothing to do with it. Measured upstream on a loaded machine: thirty seconds of
/// waiting for a free thread in front of one and a half seconds of recognition. The pool also
/// grows slowly on purpose, so a blocked worker is not given back for hundreds of milliseconds.
/// </para>
/// <para>
/// The cost is a thread start per call - microseconds - and it buys the removal of waits
/// measured in tens of seconds. Tenths traded for tens, exactly as upstream traded them.
/// </para>
/// </remarks>
internal static class DedicatedThread
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return completion.Task;
    }
}
