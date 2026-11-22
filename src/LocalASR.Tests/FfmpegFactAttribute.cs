using System.Diagnostics;

namespace LocalASR.Tests;

/// <summary>
/// A fact that needs <c>ffmpeg</c>, the optional decoder for everything but WAV, and is skipped
/// where it is not installed - the same way <see cref="InstalledModelFactAttribute"/> skips.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "ffmpeg is not installed.";
        }
    }

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            return Run("-version") == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    /// <summary>Encode a fixture into another container, by its extension.</summary>
    public static void Convert(string from, string to) =>
        Assert.Equal(0, Run("-nostdin", "-v", "error", "-y", "-i", from, to));

    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
