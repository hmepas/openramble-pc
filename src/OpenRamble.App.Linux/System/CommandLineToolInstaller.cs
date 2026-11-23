namespace OpenRamble.App.Linux;

/// <summary>
/// Puts <c>openramble</c> on the person's PATH for the command-line tool: a link in
/// <c>~/.local/bin</c> to this app's own executable, which hands it any arguments
/// (see <see cref="Program"/>).
/// </summary>
/// <remarks>
/// <para>
/// Ported from upstream's <c>CommandLineToolInstaller.swift</c>. There the link points at a
/// separate <c>openramble-cli</c> inside the app bundle; here the app's executable is the tool,
/// so the link points at it - the same thing the package's <c>/usr/bin/openramble</c> is.
/// </para>
/// <para>
/// Not ported: the refusal to install from a mounted disk image or a translocated copy. Those
/// are macOS places that vanish after a relaunch; an unpacked folder on Linux stays where it was
/// put. Added: <see cref="Uninstall"/>, which removes only a link to this app.
/// </para>
/// </remarks>
internal static class CommandLineToolInstaller
{
    public sealed class Failure(string message) : Exception(message);

    /// <summary>The file name every copy of the app has; a link to one is a link to an earlier copy.</summary>
    private const string ExecutableName = "OpenRamble.App.Linux";

    private static string LinkPath(string home) => Path.Combine(home, ".local", "bin", "openramble");

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ThisExecutable() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExecutableName);

    /// <summary>The link if it exists and points at this app's executable.</summary>
    public static string? InstalledLink(string? executable = null, string? home = null)
    {
        string link = LinkPath(home ?? Home());
        return LinkTarget(link) == (executable ?? ThisExecutable()) ? link : null;
    }

    /// <summary>
    /// Called only by the settings button. Never changes shell startup files. Replaces a link
    /// left by an earlier copy of this app; refuses anything else.
    /// </summary>
    public static string Install(string? executable = null, string? home = null)
    {
        executable ??= ThisExecutable();
        if (!File.Exists(executable)
            || OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
        {
            throw new Failure("This copy of OpenRamble does not contain the command-line tool.");
        }

        string link = LinkPath(home ?? Home());
        if (LinkTarget(link) is { } destination)
        {
            if (destination == executable) { return link; }
            if (!destination.EndsWith("/" + ExecutableName, StringComparison.Ordinal))
            {
                throw Exists(link);
            }

            File.Delete(link);
        }
        else if (Path.Exists(link))
        {
            throw Exists(link);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, executable);
        return link;
    }

    /// <summary>Remove the link if it points at this app; anything else at that path is left alone.</summary>
    public static void Uninstall(string? executable = null, string? home = null)
    {
        if (InstalledLink(executable, home) is { } link) { File.Delete(link); }
    }

    /// <summary>Whether <c>~/.local/bin</c> is on <paramref name="path"/> (this process's PATH by default).</summary>
    public static bool LinkFolderOnPath(string? home = null, string? path = null)
    {
        string folder = Path.GetDirectoryName(LinkPath(home ?? Home()))!;
        return (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.TrimEnd('/') == folder);
    }

    private static Failure Exists(string link) =>
        new($"Something else already exists at {link}. Move or remove it before installing the command-line tool.");

    /// <summary>Where a symbolic link points, or <c>null</c> when <paramref name="path"/> is not one.</summary>
    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
