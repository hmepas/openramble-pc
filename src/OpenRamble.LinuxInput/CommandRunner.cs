using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace OpenRamble.LinuxInput;

/// <summary>One run of one command-line tool, described in full.</summary>
/// <param name="Executable">Resolved through <c>PATH</c>, the way a person would type it.</param>
/// <param name="Arguments">Passed as written - never through a shell, so a space is a space.</param>
/// <param name="Input">
/// What goes to the tool's standard input, or <c>null</c> for a tool that takes none.
/// </param>
/// <param name="Budget">
/// How long the tool may take before it is killed and the run counted as a failure.
/// </param>
/// <param name="CaptureOutput">
/// Whether the tool's output is read back. Either way it is redirected away from ours - see
/// <see cref="SubprocessCommandRunner"/>.
/// </param>
internal readonly record struct CommandRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? Input = null,
    TimeSpan Budget = default,
    bool CaptureOutput = true);

/// <summary>What a tool did.</summary>
/// <param name="Started">Whether the process ran at all. <c>false</c> means it is not installed.</param>
/// <param name="ExitCode">Its status, or -1 if it never started or had to be killed.</param>
/// <param name="Output">Its standard output, empty when it was not captured.</param>
/// <param name="Failure">
/// Why it failed, in the tool's own words where there are any: the tail of its standard error, or
/// the message the operating system gave for refusing to start it. Empty on success.
/// </param>
internal readonly record struct CommandResult(bool Started, int ExitCode, string Output, string Failure)
{
    public bool Succeeded => Started && ExitCode == 0;

    /// <summary>The tool is not on this machine - an ordinary outcome, not an error.</summary>
    public static CommandResult Missing(string why) => new(false, -1, string.Empty, why);
}

/// <summary>
/// The tools this adapter drives, as much of them as it is allowed to know.
/// </summary>
/// <remarks>
/// The seam <c>IRecorderProcess</c> is for the audio path, and for the same reasons: a missing
/// binary, a compositor that refuses the virtual-keyboard protocol and a clipboard tool that hangs
/// are all failures that only a particular machine produces, and behind this they are ordinary
/// tests. It is also the guarantee that the exact command lines can be asserted argument by
/// argument, which is the only way a change to one of them is a deliberate change.
/// </remarks>
internal interface ICommandRunner
{
    CommandResult Run(CommandRequest request);
}

/// <summary>An actual subprocess, bounded in time.</summary>
/// <remarks>
/// <para>
/// <b>The text never becomes an argument.</b> Every tool here is given the dictation on standard
/// input, because a command line is world-readable in <c>/proc/&lt;pid&gt;/cmdline</c> for as long
/// as the process lives, and anything else on the machine can be watching. A dictation product
/// that puts what a person said into the process table has leaked it - quietly, and to everything.
/// </para>
/// <para>
/// <b>Output is always redirected, and only sometimes read.</b> Both halves are the same bug seen
/// from two sides, and it is <c>wl-copy</c> that has it: it forks a daemon that owns the selection
/// for as long as the offer stands, and that daemon inherits whatever standard error it was given.
/// Reading the pipe to its end would therefore block until the person copies something else -
/// minutes later, or never - so those runs are not read. But leaving the streams un-redirected is
/// worse than that: the daemon then holds <b>our</b> standard error, and a terminal that started
/// OpenRamble never gets its prompt back after the application exits. Redirected and discarded, the
/// pipe dies with this process.
/// </para>
/// </remarks>
internal sealed class SubprocessCommandRunner : ICommandRunner
{
    /// <summary>The default deadline: long enough for any of these tools, short enough to notice.</summary>
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>How much of standard error is kept for the failure message.</summary>
    private const int MaximumComplaintLength = 400;

    public CommandResult Run(CommandRequest request)
    {
        TimeSpan budget = request.Budget > TimeSpan.Zero ? request.Budget : DefaultBudget;
        var start = new ProcessStartInfo(request.Executable)
        {
            RedirectStandardInput = request.Input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // No shell: the arguments reach the tool as they are written, and a dictation with a
            // semicolon in it cannot become a second command.
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (request.Input is not null)
        {
            // UTF-8 without a byte-order mark. wl-copy would faithfully put a BOM on the clipboard,
            // and it would be pasted as an invisible character at the start of the dictation.
            start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        start.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        start.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (string argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or ObjectDisposedException)
        {
            return CommandResult.Missing($"{request.Executable} could not be started ({error.Message})");
        }

        if (process is null)
        {
            return CommandResult.Missing($"{request.Executable} could not be started");
        }

        using (process)
        {
            // Started before the input is written: a tool that answers while still being fed must
            // not fill a pipe nobody is draining, which is the classic way both sides stop.
            Task<string> output = request.CaptureOutput
                ? process.StandardOutput.ReadToEndAsync()
                : Task.FromResult(string.Empty);
            Task<string> complaint = request.CaptureOutput
                ? process.StandardError.ReadToEndAsync()
                : Task.FromResult(string.Empty);

            if (request.Input is string input)
            {
                // On a worker, because a tool that dies mid-read leaves this write blocked, and the
                // deadline below is the only thing that may decide how long this run takes.
                _ = Task.Run(() =>
                {
                    try
                    {
                        using StreamWriter stdin = process.StandardInput;
                        stdin.Write(input);
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException)
                    {
                        // The tool stopped reading. Its exit status is the diagnosis, not this.
                    }
                });
            }

            if (!process.WaitForExit((int)Math.Clamp(budget.TotalMilliseconds, 0, int.MaxValue)))
            {
                Kill(process);
                return new CommandResult(
                    true,
                    -1,
                    string.Empty,
                    $"{request.Executable} did not finish within {(int)budget.TotalMilliseconds} ms");
            }

            string said = request.CaptureOutput ? Text(output) : string.Empty;
            string wrong = request.CaptureOutput ? Text(complaint) : string.Empty;
            int status = process.ExitCode;
            return new CommandResult(
                true,
                status,
                said,
                status == 0 ? string.Empty : Trim(wrong.Length > 0 ? wrong : $"{request.Executable} exited with status {status}"));
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or Win32Exception)
        {
            // Already gone, which is the outcome asked for.
        }
    }

    /// <summary>
    /// What a stream produced, without letting a stuck reader outlive the process that fed it.
    /// </summary>
    private static string Text(Task<string> read)
    {
        try
        {
            return read.Wait(TimeSpan.FromSeconds(1)) ? read.Result : string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }

    private static string Trim(string complaint)
    {
        string said = complaint.Trim();
        return said.Length <= MaximumComplaintLength ? said : said[..MaximumComplaintLength];
    }
}
