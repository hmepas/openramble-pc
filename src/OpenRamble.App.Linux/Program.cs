using Avalonia;
using OpenRamble.AppCore;
using OpenRamble.Cli;
using OpenRamble.LinuxInput;

namespace OpenRamble.App.Linux;

/// <summary>
/// The entry point, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Everything that would look like startup work - the diagnostic log, the instance lock, the whole
/// object graph - is in <see cref="App"/>, because Avalonia is what decides when a platform exists
/// to fail against. Here there is only the builder, which is also the file every Avalonia tool
/// looks in for it.
/// </para>
/// <para>
/// There is no updater bootstrap. The Windows entry point runs Velopack's before WPF starts; this
/// port ships no Linux updater, and a bootstrap for a mechanism that does not exist would be a
/// promise that the app installs its own new versions.
/// </para>
/// <para>
/// The one exception to "nothing else" is the <c>ctl</c> verb, and it is an exception in the same
/// way <c>--diag</c> is: it is answered before Avalonia is asked for anything, because it is not
/// this application running - it is a two-line client that talks to the copy that already is. See
/// <see cref="ControlSocketClient"/>. It lives on this binary rather than beside it so that a
/// compositor keybind can name the one path a person already has. The verb stays reserved even
/// when a file named <c>ctl</c> sits in the working directory; <c>openramble -- ctl</c> (or
/// <c>./ctl</c>) transcribes that file, as the tool's help says.
/// </para>
/// <para>
/// The command-line tool is the other exception, for the same reason: <c>openramble file.m4a</c>
/// names the command a person already has on PATH. Any argument but the app's own
/// <c>--diag</c> goes to <see cref="CommandLineTool"/>, which never touches Avalonia; with no
/// arguments - the desktop entry, autostart - this is the app, as it always was.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] arguments)
    {
        if (arguments is [ControlSocketClient.Verb, .. string[] command])
        {
            return ControlSocketClient.Run(command, Console.Out, Console.Error);
        }

        if (CommandLineTool.Handles(arguments, DiagnosticLog.CommandLineFlag))
        {
            return CommandLineTool.Run(arguments);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments);
        return 0;
    }

    /// <summary>The builder, by the name Avalonia's own tooling expects to find it under.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
