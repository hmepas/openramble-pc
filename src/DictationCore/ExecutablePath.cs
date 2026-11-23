namespace DictationCore;

/// <summary>
/// Where an external tool lives, found the way a shell finds it - minus the working directory.
/// </summary>
/// <remarks>
/// <para>
/// Not a port of anything upstream: it exists because .NET's own lookup for
/// <c>Process.Start("ffmpeg")</c> on Unix tries the current directory before <c>PATH</c>, so a file
/// named <c>ffmpeg</c> in whatever folder a person ran the app from would run in its place. Every
/// launch of an external tool resolves its name here first and starts the absolute path it gets
/// back.
/// </para>
/// <para>
/// Only absolute <c>PATH</c> entries count. An empty or relative entry means "the current
/// directory" to a shell, which is the very lookup this refuses.
/// </para>
/// </remarks>
public static class ExecutablePath
{
    /// <summary>
    /// The absolute path of <paramref name="name"/> in the first absolute <c>PATH</c> entry that
    /// holds it as an executable file, or <c>null</c> when none does.
    /// </summary>
    /// <param name="name">A bare tool name such as <c>ffmpeg</c>; a name with a slash is refused.</param>
    /// <param name="searchPath">The <c>PATH</c> to walk; this process's own when <c>null</c>.</param>
    public static string? Find(string name, string? searchPath = null)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\0')) { return null; }

        searchPath ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in searchPath.Split(Path.PathSeparator))
        {
            if (directory.Length == 0 || !Path.IsPathRooted(directory)) { continue; }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (IsExecutableFile(candidate)) { return candidate; }
        }

        return null;
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            // File.Exists is false for a directory, so a folder named like the tool is passed over.
            if (!File.Exists(path)) { return false; }
            if (OperatingSystem.IsWindows()) { return true; }

            const UnixFileMode anyExecute =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
