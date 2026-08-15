using Avalonia;

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
/// </remarks>
public static class Program
{
    public static void Main(string[] arguments) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments);

    /// <summary>The builder, by the name Avalonia's own tooling expects to find it under.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
