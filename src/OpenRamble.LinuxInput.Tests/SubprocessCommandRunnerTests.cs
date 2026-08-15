using System.Diagnostics;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// The one part of the insertion path that starts real processes, checked against real ones.
/// </summary>
/// <remarks>
/// Everything above this seam is tested against a desktop that does not exist, which is what makes
/// those tests fast and exhaustive - and which means nothing there would notice if the seam itself
/// stopped feeding standard input, stopped killing what it started, or stopped keeping the tools'
/// pipes off ours. These use <c>cat</c>, <c>sh</c> and <c>sleep</c>, which are on every Linux this
/// runs on, and take a fraction of a second.
/// </remarks>
[Collection(LinuxInputCollection.Name)]
public sealed class SubprocessCommandRunnerTests
{
    [LinuxFact]
    public void StandardInputReachesTheTool()
    {
        // The whole reason the dictation is not an argument: it goes down this pipe instead.
        var runner = new SubprocessCommandRunner();

        CommandResult result = runner.Run(new CommandRequest(
            "cat",
            [],
            "what the person said",
            TimeSpan.FromSeconds(5)));

        Assert.True(result.Succeeded);
        Assert.Equal("what the person said", result.Output);
    }

    [LinuxFact]
    public void AMissingToolIsAnOrdinaryOutcome()
    {
        var runner = new SubprocessCommandRunner();

        CommandResult result = runner.Run(new CommandRequest(
            "openramble-has-no-such-tool",
            [],
            Budget: TimeSpan.FromSeconds(5)));

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Contains("openramble-has-no-such-tool", result.Failure, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AToolThatRefusesSaysWhyInItsOwnWords()
    {
        var runner = new SubprocessCommandRunner();

        CommandResult result = runner.Run(new CommandRequest(
            "sh",
            ["-c", "echo 'Compositor does not support the virtual keyboard protocol' >&2; exit 1"],
            Budget: TimeSpan.FromSeconds(5)));

        Assert.True(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        // No sentence of ours improves on the tool's own diagnosis.
        Assert.Contains("virtual keyboard protocol", result.Failure, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AToolThatWedgesIsKilledAtTheDeadline()
    {
        var runner = new SubprocessCommandRunner();
        long began = Stopwatch.GetTimestamp();

        CommandResult result = runner.Run(new CommandRequest(
            "sleep",
            ["30"],
            Budget: TimeSpan.FromMilliseconds(300)));

        Assert.False(result.Succeeded);
        Assert.Contains("did not finish", result.Failure, StringComparison.Ordinal);
        // A dictation must not be able to hang on a tool that stopped answering.
        Assert.True(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5));
    }

    [LinuxFact]
    public void AToolThatLeavesADaemonBehindDoesNotHoldTheRunOpen()
    {
        // This is wl-copy: it forks a process that owns the clipboard offer and keeps the standard
        // error it inherited. Waiting for that pipe to end means waiting for the person to copy
        // something else, and letting the daemon inherit ours means a terminal that never gets its
        // prompt back after OpenRamble exits. Neither may happen.
        var runner = new SubprocessCommandRunner();
        long began = Stopwatch.GetTimestamp();

        CommandResult result = runner.Run(new CommandRequest(
            "sh",
            ["-c", "sleep 20 & exit 0"],
            "content for the clipboard",
            TimeSpan.FromSeconds(10),
            CaptureOutput: false));

        Assert.True(result.Succeeded);
        Assert.True(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// A fact that starts real processes, and is skipped where those processes mean something else.
/// </summary>
/// <remarks>
/// The adapters' logic is platform-neutral and its tests run anywhere; these are about how Linux
/// starts, feeds and kills a tool, and asserting them on another operating system would assert that
/// operating system.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "These check how Linux starts and stops the tools the insertion path drives.";
        }
    }
}
