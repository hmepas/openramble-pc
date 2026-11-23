using Avalonia.Headless.XUnit;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Tests;

namespace OpenRamble.App.Linux.UiTests;

/// <summary>Settings comes up on each of its pages without throwing.</summary>
public sealed class SettingsWindowTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    [AvaloniaTheory]
    [InlineData("general")]
    [InlineData("setup")]
    [InlineData("dictionary")]
    [InlineData("about")]
    public void EachPageOpens(string page)
    {
        AppState state = harness.MakeState();
        var window = new SettingsWindow(state);
        window.Show();
        window.SelectPage(page);
        Ui.Settle();

        Assert.True(window.IsVisible);
        window.Close();
        Ui.Settle();
    }
}
