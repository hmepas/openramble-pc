using System.Diagnostics;

namespace LocalASR.Tests;

public sealed class DictationPriorityTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"priority-{Guid.NewGuid():D}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The other process is Python's <c>fcntl.lockf</c>, as upstream's - the lock a script, or an
    /// app built on anything else, would take.
    /// </summary>
    [Fact]
    public void AnotherProcessIsVisibleAndTerminationReleasesPriority()
    {
        if (OperatingSystem.IsWindows()) { return; }

        string path = Path.Combine(folder, "priority.lock");
        using var priority = new DictationPriority(path);
        Assert.False(priority.IsActive());
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import fcntl,sys; f=open(sys.argv[1],'r+'); fcntl.lockf(f,fcntl.LOCK_EX); print('ready',flush=True); sys.stdin.read()");
        start.ArgumentList.Add(path);
        using Process child = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", child.StandardOutput.ReadLine());
            Assert.True(priority.IsActive());
            child.Kill();
            child.WaitForExit();
            Assert.False(priority.IsActive());
            priority.SetActive(true);
            Assert.True(priority.IsActive());
            priority.SetActive(false);
            Assert.False(priority.IsActive());
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
        }
    }

    /// <summary>Port addition: a lock another process holds refuses the app's claim instead of waiting.</summary>
    [Fact]
    public void AHeldLockRefusesASecondClaimWithoutWaiting()
    {
        if (OperatingSystem.IsWindows()) { return; }

        string path = Path.Combine(folder, "priority.lock");
        using var priority = new DictationPriority(path);
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import fcntl,sys; f=open(sys.argv[1],'r+'); fcntl.lockf(f,fcntl.LOCK_EX,1); print('ready',flush=True); sys.stdin.read()");
        start.ArgumentList.Add(path);
        using Process child = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", child.StandardOutput.ReadLine());
            Assert.Throws<IOException>(() => priority.SetActive(true));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [Fact]
    public async Task WaitingEndsWhenDictationEnds()
    {
        using var priority = new DictationPriority(Path.Combine(folder, "priority.lock"));
        priority.SetActive(true);
        Task waiting = priority.WaitForTurn();
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);
        priority.SetActive(false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitingIsCancelled()
    {
        using var priority = new DictationPriority(Path.Combine(folder, "priority.lock"));
        priority.SetActive(true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => priority.WaitForTurn(cancellation.Token));
    }

    [Fact]
    public void ASymbolicLinkIsRefused()
    {
        if (OperatingSystem.IsWindows()) { return; }

        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "elsewhere");
        File.WriteAllText(target, "");
        string link = Path.Combine(folder, "priority.lock");
        File.CreateSymbolicLink(link, target);
        Assert.Throws<IOException>(() => new DictationPriority(link));
    }
}
