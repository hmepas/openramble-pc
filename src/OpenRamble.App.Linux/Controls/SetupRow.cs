using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// One line of a setup list (upstream <c>SetupRow</c>): the state's glyph, the name with a
/// sentence under it, an optional thin progress bar under the words, and the one thing to do
/// about it on the column's edge - ink when it is the next step, quiet otherwise. Done, the glyph
/// is a green check and the action is gone (set <see cref="ActionText"/> to <c>null</c>).
/// </summary>
public sealed class SetupRow : UserControl
{
    public static readonly StyledProperty<StateGlyphKind> GlyphProperty =
        AvaloniaProperty.Register<SetupRow, StateGlyphKind>(nameof(Glyph));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SetupRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<SetupRow, string?>(nameof(Detail));

    /// <summary>0…1 under the words; <c>null</c> hides the bar.</summary>
    public static readonly StyledProperty<double?> ProgressProperty =
        AvaloniaProperty.Register<SetupRow, double?>(nameof(Progress));

    /// <summary>The button's words, e.g. <see cref="DownloadLabel"/>; <c>null</c> hides the button.</summary>
    public static readonly StyledProperty<string?> ActionTextProperty =
        AvaloniaProperty.Register<SetupRow, string?>(nameof(ActionText));

    /// <summary>The button is the next step: ink. Otherwise quiet.</summary>
    public static readonly StyledProperty<bool> IsPrimaryActionProperty =
        AvaloniaProperty.Register<SetupRow, bool>(nameof(IsPrimaryAction));

    private readonly StateGlyph glyph = new() { GlyphSize = 15, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 0, 14) };
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineSpacing = 1.5, Margin = new Thickness(0, 3, 0, 0) };
    private readonly ProgressBar bar = new() { Classes = { "thin" }, Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button action = new() { VerticalAlignment = VerticalAlignment.Center, MinHeight = 34 };

    public SetupRow()
    {
        ThemeTokens.Bind(title, TextBlock.ForegroundProperty, ThemeTokens.Label);
        ThemeTokens.Bind(detail, TextBlock.ForegroundProperty, ThemeTokens.Label2);
        action.Click += (_, e) => ActionClick?.Invoke(this, e);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14), Children = { title, detail, bar } };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
            MinHeight = 52,
            Children = { glyph, words, action },
        };
        Grid.SetColumn(words, 1);
        Grid.SetColumn(action, 2);
        Content = grid;
        Sync();
    }

    /// <summary>The button was pressed.</summary>
    public event EventHandler<RoutedEventArgs>? ActionClick;

    public StateGlyphKind Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public double? Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public string? ActionText
    {
        get => GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public bool IsPrimaryAction
    {
        get => GetValue(IsPrimaryActionProperty);
        set => SetValue(IsPrimaryActionProperty, value);
    }

    /// <summary>The button itself, for a tooltip, an accessible name or <c>IsEnabled</c>.</summary>
    public Button ActionButton => action;

    /// <summary>"Download (486 MB)": the size is on the button that spends it.</summary>
    public static string DownloadLabel(int megabytes) => $"Download ({megabytes} MB)";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphProperty || change.Property == TitleProperty || change.Property == DetailProperty
            || change.Property == ProgressProperty || change.Property == ActionTextProperty
            || change.Property == IsPrimaryActionProperty)
        {
            Sync();
        }
    }

    private void Sync()
    {
        glyph.Kind = Glyph;
        title.Text = Title;
        detail.Text = Detail;
        detail.IsVisible = !string.IsNullOrEmpty(Detail);
        bar.IsVisible = Progress is not null;
        bar.Value = Progress ?? 0;
        action.Content = ActionText;
        action.IsVisible = ActionText is not null;
        action.Classes.Set("ink", IsPrimaryAction);
        action.Classes.Set("quiet", !IsPrimaryAction);
    }
}
