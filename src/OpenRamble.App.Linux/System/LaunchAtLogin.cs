using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// Start at login, through an XDG autostart entry.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of the Windows <c>HKCU\...\Run</c> value, and chosen for the same reasons: it
/// needs no elevation, it is the one place a person looks - GNOME's Tweaks, KDE's "Autostart" page
/// and every session manager read this directory - and deleting the file there must win over
/// whatever this application believes. That is why <see cref="IsEnabled"/> looks at the file every
/// time rather than trusting a saved setting: the directory is the truth, and a settings file that
/// disagreed with it would show the person a switch that lies.
/// </para>
/// <para>
/// <b>A systemd user unit was the other candidate and is not used.</b> It is the more modern
/// mechanism, but it is invisible in every desktop's own startup list, it needs the session's
/// D-Bus environment imported before a graphical process can talk to the compositor, and it is one
/// more thing that fails differently on a machine without systemd. The autostart directory is a
/// specification every desktop implements and a text file a person can read.
/// </para>
/// </remarks>
public sealed class LaunchAtLogin : ILaunchAtLoginService
{
    /// <summary>The file name, fixed: it is how the entry is recognized again on the next run.</summary>
    internal const string EntryFileName = "openramble.desktop";

    private readonly string directory;
    private readonly Func<string?> executablePath;

    public LaunchAtLogin()
        : this(AutostartDirectory(), () => Environment.ProcessPath)
    {
    }

    /// <summary>The same service pointed at a directory of the test's choosing.</summary>
    internal LaunchAtLogin(string directory, Func<string?> executablePath)
    {
        this.directory = directory;
        this.executablePath = executablePath;
    }

    public bool IsEnabled => File.Exists(EntryPath);

    private string EntryPath => Path.Combine(directory, EntryFileName);

    /// <summary>
    /// <c>$XDG_CONFIG_HOME/autostart</c>, falling back to the specification's default.
    /// </summary>
    /// <remarks>
    /// The variable is honoured rather than assumed away: a person who moved their configuration
    /// has moved this with it, and writing to <c>~/.config</c> anyway would leave an entry nothing
    /// reads.
    /// </remarks>
    internal static string AutostartDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = configured is { Length: > 0 } && Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        return Path.Combine(root, "autostart");
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Deleting a file is quiet about the file already being gone but not about its
            // directory, and a person who has never switched this on has no autostart directory at
            // all. Nothing to remove is the state that was asked for, not a failure.
            if (Directory.Exists(directory)) { File.Delete(EntryPath); }
            return;
        }

        string executable = executablePath()
            ?? throw new InvalidOperationException("the application's own path is unknown");

        Directory.CreateDirectory(directory);
        File.WriteAllText(EntryPath, Entry(executable));
    }

    /// <summary>
    /// The entry itself, as the desktop entry specification wants it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Terminal=false</c> and <c>Type=Application</c> are required by the specification.
    /// <c>X-GNOME-Autostart-enabled</c> is not, and is written anyway: GNOME's own startup editor
    /// writes it, and an entry without it is shown there as a row a person cannot switch off.
    /// </para>
    /// <para>
    /// <b>The path is quoted the way the specification says, and that is the whole escaping
    /// story.</b> A home directory with a space in it is ordinary, and an unquoted <c>Exec</c>
    /// would autostart the first word of it. Inside the quotes only the backslash and the quote
    /// itself have to be escaped; the specification's reserved characters - <c>$</c>, backtick -
    /// are literal within a quoted argument.
    /// </para>
    /// </remarks>
    internal static string Entry(string executable) =>
        "[Desktop Entry]\n"
        + "Type=Application\n"
        + "Name=OpenRamble\n"
        + "Comment=Hold a key, speak, let go\n"
        + $"Exec={QuoteExec(executable)}\n"
        + "Terminal=false\n"
        + "X-GNOME-Autostart-enabled=true\n";

    private static string QuoteExec(string executable) =>
        "\"" + executable.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
