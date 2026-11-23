using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRamble.App.Linux;

/// <summary>
/// <c>XCURSOR_THEME</c> and <c>XCURSOR_SIZE</c>, filled in from the desktop's own cursor settings
/// when whoever started the app did not export them.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia runs as an X11 client (under XWayland on a Wayland desktop), and an X11 client themes
/// its own cursor: the arrow is an X font cursor that libXcursor replaces with a themed image,
/// picked by those two variables. A Wayland compositor draws its own cursor from its own settings,
/// so nothing guarantees the variables are in the environment - a systemd user unit or a desktop
/// that never exports them starts the app without them, and libXcursor then falls back to its
/// "default" theme at a size guessed from the screen: a foreign, oversized arrow over this window
/// only. So the gaps are filled from the same places the desktop's own toolkits read.
/// </para>
/// <para>
/// A value somebody did set, even one this would not have chosen, is left alone. And it has to be
/// written with libc's <c>setenv</c>: on Linux <see cref="Environment.SetEnvironmentVariable(string, string)"/>
/// changes only .NET's copy, while libXcursor reads the native environment with <c>getenv</c>.
/// </para>
/// </remarks>
internal static class CursorEnvironment
{
    internal const int DefaultSize = 24;

    /// <summary>The longest the whole lookup may spend waiting on <c>gsettings</c>.</summary>
    private static readonly TimeSpan GsettingsBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>What to write: <c>null</c> for a variable that is to be left as it is.</summary>
    internal readonly record struct Fill(string? Size, string? Theme);

    /// <summary>Fill the variables in this process's native environment. Call before Avalonia starts.</summary>
    internal static void Apply()
    {
        var deadline = Stopwatch.StartNew();
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        Fill fill = Resolve(
            Environment.GetEnvironmentVariable,
            key => Gsettings(key, GsettingsBudget - deadline.Elapsed),
            () => ReadOrNull(Path.Combine(configHome, "gtk-3.0", "settings.ini")));

        // Overwrite is safe: Resolve only proposes a value for a variable that is unset or empty,
        // and an empty one would survive setenv's no-overwrite mode.
        if (fill.Size is not null) { _ = setenv("XCURSOR_SIZE", fill.Size, 1); }
        if (fill.Theme is not null) { _ = setenv("XCURSOR_THEME", fill.Theme, 1); }
    }

    /// <summary>
    /// The values to fill, from the given sources. No I/O of its own: the sources are asked lazily,
    /// so <c>gsettings</c> is not run for a variable that is already set.
    /// </summary>
    /// <param name="environment">A variable's value, or <c>null</c>.</param>
    /// <param name="gsettings">The raw output of <c>gsettings get org.gnome.desktop.interface KEY</c>, or <c>null</c>.</param>
    /// <param name="gtkSettings">The text of gtk-3.0's <c>settings.ini</c>, or <c>null</c>.</param>
    internal static Fill Resolve(
        Func<string, string?> environment,
        Func<string, string?> gsettings,
        Func<string?> gtkSettings)
    {
        string? ini = null;
        bool iniRead = false;
        string? Ini(string key)
        {
            if (!iniRead) { ini = gtkSettings(); iniRead = true; }
            return IniValue(ini, key);
        }

        string? size = null;
        if (string.IsNullOrEmpty(environment("XCURSOR_SIZE")))
        {
            size = PositiveInt(environment("HYPRCURSOR_SIZE"))
                ?? PositiveInt(GsettingsValue(gsettings("cursor-size")))
                ?? PositiveInt(Ini("gtk-cursor-theme-size"))
                ?? DefaultSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string? theme = null;
        if (string.IsNullOrEmpty(environment("XCURSOR_THEME")))
        {
            theme = NonEmpty(GsettingsValue(gsettings("cursor-theme")))
                ?? NonEmpty(Ini("gtk-cursor-theme-name"));
        }

        return new Fill(size, theme);
    }

    /// <summary><c>uint32 24</c>, <c>24</c> or <c>'Adwaita'</c> as gsettings prints them, bare.</summary>
    internal static string? GsettingsValue(string? output)
    {
        if (output is null) { return null; }
        string value = output.Trim();
        if (value.StartsWith("uint32 ", StringComparison.Ordinal) || value.StartsWith("int32 ", StringComparison.Ordinal))
        {
            value = value[(value.IndexOf(' ') + 1)..].Trim();
        }

        return Unquote(value);
    }

    /// <summary>A <c>key=value</c> line's value from a GTK settings file.</summary>
    internal static string? IniValue(string? text, string key)
    {
        if (text is null) { return null; }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[') { continue; }
            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim() == key) { return Unquote(line[(equals + 1)..].Trim()); }
        }

        return null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"' ? value[1..^1] : value;

    private static string? PositiveInt(string? value) =>
        int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number > 0
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One gsettings key, or <c>null</c> - not installed, no schema, too slow, or failed.</summary>
    private static string? Gsettings(string key, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) { return null; }

        // The absolute path, so the working directory is never searched for the tool.
        string? resolved = DictationCore.ExecutablePath.Find("gsettings");
        if (resolved is null) { return null; }

        var start = new ProcessStartInfo(resolved)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("get");
        start.ArgumentList.Add("org.gnome.desktop.interface");
        start.ArgumentList.Add(key);

        try
        {
            using Process? started = Process.Start(start);
            if (started is null) { return null; }
            Task<string> output = started.StandardOutput.ReadToEndAsync();
            _ = started.StandardError.ReadToEndAsync();
            if (!started.WaitForExit(timeout))
            {
                try { started.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            return started.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);
}
