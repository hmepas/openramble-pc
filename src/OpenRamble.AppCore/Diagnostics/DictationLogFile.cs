using System.Collections.Concurrent;
using System.Globalization;

namespace OpenRamble.AppCore;

/// <summary>
/// A plain text log a person can find, open, and send to someone.
///
/// <see cref="DiagnosticLog"/> already has everything, but it is a developer's switch: an
/// environment variable or a command-line flag naming a file, for the run being debugged.
/// Someone testing this app and hitting a slow dictation should be able to flip a setting,
/// open a folder and send what they see - which is what this is.
///
/// Off unless asked for. When off nothing is opened, nothing is written, and no file exists -
/// a log that appears without being asked for is a file someone did not consent to.
///
/// Never contains dictated text. Numbers, stages and reasons only, exactly like the diagnostic
/// log line it mirrors. That is not a policy this class is free to relax: it is written down in
/// PORTING.md and checked in review.
/// </summary>
/// <remarks>
/// <para>
/// Where it lives differs from the Mac, which has <c>~/Library/Logs</c>. A PC has no folder a
/// person would look in for logs, so they go beside the app's other files:
/// <c>&lt;Support&gt;/Logs</c>, see <see cref="DirectoryUnder"/>. The "Open Logs" button is
/// what makes the location findable; the path is not something anyone has to remember.
/// </para>
/// <para>
/// Its own thread: appending to a file is blocking work, and this app has spent a long time
/// learning not to do that on the pool a dictation waits on. Logging that slows dictation would
/// be a poor way to study why dictation is slow.
/// </para>
/// </remarks>
public sealed class DictationLogFile : IDisposable
{
    private const string FolderName = "Logs";

    private readonly string directory;
    private readonly Func<DateTimeOffset> now;
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread writer;
    private bool disposed;

    /// <param name="directory">Where the daily files go. Created on the first write, never before.</param>
    /// <param name="now">The clock, injectable so the day and the stamp can be checked.</param>
    public DictationLogFile(string directory, Func<DateTimeOffset>? now = null)
    {
        this.directory = directory;
        this.now = now ?? (static () => DateTimeOffset.Now);
        writer = new Thread(Drain)
        {
            IsBackground = true,
            Name = "openramble-log-file",
        };
        writer.Start();
    }

    /// <summary>The folder under the application's support directory.</summary>
    public static string DirectoryUnder(AppPaths paths) => Path.Combine(paths.Support(), FolderName);

    public string Directory => directory;

    /// <summary>
    /// Set from the setting; read on every line so the switch takes effect at once rather than
    /// at the next launch.
    /// </summary>
    public bool IsEnabled
    {
        get => Volatile.Read(ref isEnabled);
        set => Volatile.Write(ref isEnabled, value);
    }

    private bool isEnabled;

    /// <summary>
    /// One file per day. A single growing file is impossible to send once it is large, and
    /// rotating by size loses the day someone wants to talk about.
    /// </summary>
    public string CurrentPath => PathFor(now());

    /// <summary>Today's file if it exists, otherwise the newest one, otherwise <c>null</c>.</summary>
    public string? NewestPath()
    {
        if (!System.IO.Directory.Exists(directory)) { return null; }
        return System.IO.Directory.EnumerateFiles(directory, "dictation-*.log")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public void Write(string line)
    {
        if (!IsEnabled) { return; }
        DateTimeOffset at = now();
        string stamped = at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine;
        string path = PathFor(at);
        Enqueue(() =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                File.AppendAllText(path, stamped);
            }
            catch (IOException)
            {
                // A log that can take the application down is worse than no log.
            }
            catch (UnauthorizedAccessException)
            {
            }
        });
    }

    /// <summary>
    /// Delete every log this app has written.
    ///
    /// Offered because the person who turned this on should be able to take it back, and "off"
    /// that leaves files behind is not off.
    /// </summary>
    public void RemoveAll() =>
        Enqueue(() =>
        {
            try
            {
                if (System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        });

    /// <summary>Wait until everything queued so far has reached the disk. For the tests.</summary>
    public void Flush()
    {
        using var done = new ManualResetEventSlim(false);
        if (!Enqueue(() => done.Set())) { return; }
        done.Wait();
    }

    public void Dispose()
    {
        lock (queue)
        {
            if (disposed) { return; }
            disposed = true;
        }

        queue.CompleteAdding();
        writer.Join();
        queue.Dispose();
    }

    private bool Enqueue(Action work)
    {
        lock (queue)
        {
            if (disposed) { return false; }
            queue.Add(work);
            return true;
        }
    }

    private void Drain()
    {
        foreach (Action work in queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    private string PathFor(DateTimeOffset at) =>
        Path.Combine(directory, "dictation-" + at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
}
