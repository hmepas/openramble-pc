using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenRamble.App.Linux;

/// <summary>
/// One question with a destructive answer, asked before the thing is done.
/// </summary>
/// <remarks>
/// The Windows shell asks with <c>MessageBox.Show</c>; the macOS original with a confirmation
/// dialog. Avalonia has neither, so this is one, for the two places that need it - deleting
/// the model, which costs a download, and deleting the whole history, which costs recordings.
/// A dialog rather than a <see cref="Notice"/>: this one has an owner and an answer.
/// </remarks>
internal static class Confirm
{
    /// <summary><c>true</c> - the person chose the destructive action.</summary>
    internal static async Task<bool> Ask(Window owner, string title, string message, string actionTitle)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 4, 16, 4),
            IsCancel = true,
        };
        var confirm = new Button
        {
            Content = actionTitle,
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = StatusBrushes.Attention,
        };

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) },
                    text,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        bool answer = false;
        cancel.Click += (_, _) => window.Close();
        confirm.Click += (_, _) =>
        {
            answer = true;
            window.Close();
        };

        await window.ShowDialog(owner).ConfigureAwait(true);
        return answer;
    }
}
