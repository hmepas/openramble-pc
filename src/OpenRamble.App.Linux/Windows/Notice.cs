using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenRamble.App.Linux;

/// <summary>
/// Saying something out loud when there is no window to say it in.
/// </summary>
/// <remarks>
/// <para>
/// The Windows shell calls <c>MessageBox.Show</c> for exactly two things: a diagnostic log that
/// could not be opened, and a tray icon the shell refused. Both are failures of the app's own
/// plumbing, both happen before or outside any window of ours, and both would otherwise be
/// invisible - which for a tray-only application means a process that is running and showing
/// nothing at all. Avalonia has no message box, so this is one, at the size those two messages
/// need and no larger.
/// </para>
/// <para>
/// Not a dialog: <c>ShowDialog</c> needs an owner window, and the whole point of these two
/// messages is that there is not one.
/// </para>
/// </remarks>
internal static class Notice
{
    internal static void Show(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        var close = new Button
        {
            Content = "OK",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            IsDefault = true,
        };

        var window = new Window
        {
            Title = "OpenRamble",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Children = { text, close },
            },
        };

        close.Click += (_, _) => window.Close();
        window.Show();
    }
}
