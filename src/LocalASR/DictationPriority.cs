using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalASR;

/// <summary>
/// One process-owned lock signals interactive dictation. The app holds it for a take; a
/// background file job probes it before every piece and waits while it is held.
/// </summary>
/// <remarks>
/// <para>
/// On Linux, as in the Swift original, the lock is a POSIX record lock (<c>fcntl</c>) on the
/// first byte of the file, and the probe is <c>F_GETLK</c>, which asks without acquiring - so a
/// probe can never be what makes the app's own <c>F_SETLK</c> fail at key-down. The kernel
/// drops the lock when its process dies, so a crashed app never leaves a job paused. Keep one
/// instance per process: closing any descriptor for a POSIX-locked file releases that process's
/// locks. Never unlink the rendezvous file.
/// </para>
/// <para>
/// On Windows the lock is <see cref="FileStream.Lock"/> (<c>LockFileEx</c>), also released when
/// its process dies. Windows has no way to ask without acquiring, so the probe takes and
/// returns the byte; a key-down that lands inside those microseconds finds it taken and the app
/// reports it could not pause background work. Untested on Windows so far.
/// </para>
/// <para>
/// The Swift original also refuses a rendezvous file owned by another user. .NET has no owner
/// query, and the file lives in the user's own data folder; a symbolic link or anything but a
/// regular file is still refused.
/// </para>
/// </remarks>
public sealed partial class DictationPriority : IDisposable
{
    /// <summary>The file name, in the app's data folder.</summary>
    public const string FileName = "dictation-priority.lock";

    private readonly Lock mutex = new();
    private readonly FileStream file;
    private bool held;

    /// <param name="path">The rendezvous file; created if missing, along with its folder.</param>
    public DictationPriority(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var info = new FileInfo(path);
        if (info.Exists && (info.LinkTarget is not null || (info.Attributes & FileAttributes.Directory) != 0))
        {
            throw new IOException("the dictation priority file is not a regular file");
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        file = new FileStream(path, options);
    }

    public void Dispose()
    {
        lock (mutex)
        {
            file.Dispose();
            held = false;
        }
    }

    /// <summary>Hold the lock while a take runs, release it when the take ends.</summary>
    /// <exception cref="IOException">Another process holds the lock, or the system refused it.</exception>
    public void SetActive(bool active)
    {
        lock (mutex)
        {
            if (active == held) { return; }
            if (OperatingSystem.IsWindows())
            {
                if (active) { file.Lock(0, 1); }
                else { file.Unlock(0, 1); }
            }
            else
            {
                var claim = Claim(active ? Native.WriteLock : Native.Unlock);
                if (Native.fcntl(file.SafeFileHandle, Native.SetLock, ref claim) != 0)
                {
                    throw Failure();
                }
            }

            held = active;
        }
    }

    /// <summary>Is someone dictating - this process or another?</summary>
    public bool IsActive()
    {
        lock (mutex)
        {
            if (held) { return true; }
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    file.Lock(0, 1);
                }
                catch (IOException)
                {
                    return true;
                }

                file.Unlock(0, 1);
                return false;
            }

            var query = Claim(Native.WriteLock);
            if (Native.fcntl(file.SafeFileHandle, Native.GetLock, ref query) != 0)
            {
                throw Failure();
            }

            return query.Type != Native.Unlock;
        }
    }

    /// <summary>Return once nobody is dictating, checking every 50 ms.</summary>
    public async Task WaitForTurn(CancellationToken cancellationToken = default)
    {
        while (IsActive())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Native.Flock Claim(short type) => new() { Type = type, Whence = 0, Start = 0, Length = 1 };

    private static IOException Failure() =>
        new($"the dictation priority lock failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");

    /// <summary>
    /// <c>fcntl(2)</c> record locks with <c>struct flock</c> as glibc lays it out on 64-bit Linux
    /// (x86-64 and AArch64 alike).
    /// </summary>
    private static partial class Native
    {
        public const int GetLock = 5;
        public const int SetLock = 6;
        public const short WriteLock = 1;
        public const short Unlock = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct Flock
        {
            public short Type;
            public short Whence;
            public long Start;
            public long Length;
            public int Pid;
        }

        // Variadic in C; the third argument is a pointer, which 64-bit Linux passes exactly as a
        // fixed argument.
        [LibraryImport("libc", SetLastError = true)]
        public static partial int fcntl(SafeFileHandle descriptor, int command, ref Flock flock);
    }
}
