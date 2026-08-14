using Velopack;

namespace OpenRamble.App;

/// <summary>
/// Explicit entry point so Velopack's bootstrap runs before WPF.
/// </summary>
/// <remarks>
/// <c>VelopackApp.Build().Run()</c> handles the installer lifecycle moments
/// (first run after install creates the Start-menu shortcut, uninstall removes
/// it, a newer Setup.exe swaps the version in place). It is inert when the app
/// runs from a plain folder or zip, so the unpackaged flow is unchanged.
/// </remarks>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
