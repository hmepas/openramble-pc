using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenRamble.App.Linux;

/// <summary>
/// A group on a page (upstream <c>BlockSection</c>): what it is about, a hairline, its rows, and
/// a sentence under them after another hairline. No box: the space around the group is the group.
/// Header and footer are optional; the rows are the items (children in markup).
/// </summary>
public sealed class BlockSection : ItemsControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<BlockSection, string?>(nameof(Header));

    public static readonly StyledProperty<string?> FooterProperty =
        AvaloniaProperty.Register<BlockSection, string?>(nameof(Footer));

    public BlockSection()
    {
        Template = new FuncControlTemplate<BlockSection>((section, scope) =>
        {
            var header = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
            var footer = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineSpacing = 2, Margin = new Thickness(0, 12, 0, 0) };
            ThemeTokens.Bind(header, TextBlock.ForegroundProperty, ThemeTokens.Label2);
            ThemeTokens.Bind(footer, TextBlock.ForegroundProperty, ThemeTokens.Label2);
            Border headerLine = Hairline();
            Border footerLine = Hairline();
            header.Bind(TextBlock.TextProperty, section.GetObservable(HeaderProperty));
            footer.Bind(TextBlock.TextProperty, section.GetObservable(FooterProperty));
            IObservable<bool> hasHeader = section.GetObservable(HeaderProperty, text => text is not null);
            IObservable<bool> hasFooter = section.GetObservable(FooterProperty, text => text is not null);
            header.Bind(IsVisibleProperty, hasHeader);
            headerLine.Bind(IsVisibleProperty, hasHeader);
            footer.Bind(IsVisibleProperty, hasFooter);
            footerLine.Bind(IsVisibleProperty, hasFooter);
            var items = new ItemsPresenter { Name = "PART_ItemsPresenter" };
            items.RegisterInNameScope(scope);
            return new StackPanel { Children = { header, headerLine, items, footerLine, footer } };
        });
    }

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>A 0.5-unit rule in the theme's hairline colour.</summary>
    internal static Border Hairline()
    {
        var line = new Border { Height = 0.5, HorizontalAlignment = HorizontalAlignment.Stretch };
        ThemeTokens.Bind(line, Border.BackgroundProperty, ThemeTokens.Hairline);
        return line;
    }
}

/// <summary>
/// One row of a section (upstream <c>BlockRow</c>): what it is on the left - a title with an
/// optional sentence under it - and the control (<see cref="ContentControl.Content"/>) on the
/// column's right edge. At least 52 tall, 8 above and below.
/// </summary>
public sealed class BlockRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<BlockRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<BlockRow, string?>(nameof(Detail));

    public BlockRow()
    {
        Template = new FuncControlTemplate<BlockRow>((row, scope) =>
        {
            var title = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
            var detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            ThemeTokens.Bind(title, TextBlock.ForegroundProperty, ThemeTokens.Label);
            ThemeTokens.Bind(detail, TextBlock.ForegroundProperty, ThemeTokens.Label2);
            title.Bind(TextBlock.TextProperty, row.GetObservable(TitleProperty));
            detail.Bind(TextBlock.TextProperty, row.GetObservable(DetailProperty));
            detail.Bind(IsVisibleProperty, row.GetObservable(DetailProperty, text => !string.IsNullOrEmpty(text)));
            var control = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8),
                [~ContentPresenter.ContentProperty] = row[~ContentProperty],
            };
            control.RegisterInNameScope(scope);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8), Children = { title, detail } };
            Grid.SetColumn(control, 1);
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 12,
                MinHeight = 52,
                Children = { words, control },
            };
        });
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
}
