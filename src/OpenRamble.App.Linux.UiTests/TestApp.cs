using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(OpenRamble.App.Linux.UiTests.TestApp))]

namespace OpenRamble.App.Linux.UiTests;

/// <summary>
/// The real <see cref="App"/> - its theme, tokens, styles and icons - on the headless platform.
/// Without a desktop lifetime its start-up returns before the tray, the hotkeys and the instance
/// lock, so only the resources a window needs are loaded.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
