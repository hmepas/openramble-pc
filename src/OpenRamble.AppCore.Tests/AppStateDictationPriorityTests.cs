using System.Diagnostics;
using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// A take holds the lock a background <c>openramble</c> job waits on, from key-down to idle.
/// </summary>
/// <remarks>
/// Upstream wires this in <c>AppState</c> without a test of its own. The lock is a POSIX record
/// lock, which its own process never sees as held, so the probe here is another process:
/// Python's <c>fcntl.lockf</c>, the same probe upstream's CLI test takes. In the diagnostic-log
/// collection because a take writes to the process-wide log those suites read back.
/// </remarks>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateDictationPriorityTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task ATakeHoldsTheDictationPriorityUntilItEnds()
    {
        if (OperatingSystem.IsWindows()) { return; }

        harness.InstallModelMarker();
        harness.Permissions.MicrophoneGranted = true;
        AppState state = harness.MakeState();
        await TestWait.For("the model was seen on disk", () => state.ModelStateValue.IsReady);
        string path = harness.Paths.DictationPriorityLock();
        Assert.True(AnotherProcessCanLock(path), "nobody dictates yet");

        state.PressDictationButton();
        await TestWait.For("recording started", () => state.DictationStateValue == DictationState.Listening);
        Assert.False(AnotherProcessCanLock(path), "the take holds the priority");

        state.ReleaseDictationButton();
        await TestWait.For("the session finished", () => state.DictationStateValue == DictationState.Idle);
        Assert.True(AnotherProcessCanLock(path), "an idle app releases it");
    }

    private static bool AnotherProcessCanLock(string path)
    {
        var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            "import fcntl,sys\nf=open(sys.argv[1],'r+')\ntry:\n fcntl.lockf(f,fcntl.LOCK_EX|fcntl.LOCK_NB,1); print('free')\nexcept OSError:\n print('held')");
        start.ArgumentList.Add(path);
        using Process child = Process.Start(start)!;
        string answer = child.StandardOutput.ReadToEnd().Trim();
        child.WaitForExit();
        return answer == "free";
    }
}
