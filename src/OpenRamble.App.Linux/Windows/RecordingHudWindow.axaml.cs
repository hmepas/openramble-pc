using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The pill that says a recording is running (upstream ScreenRecordingHUD.swift): a red dot, the
/// word and the clock, up for as long as anything is being captured. The tray badge alone is easy
/// to miss.
/// </summary>
/// <remarks>
/// Built like the dictation panel and for the same reason - it must never take the focus from the
/// call being recorded: <c>ShowActivated="False"</c>, not focusable, topmost, no decorations. A
/// click opens the Library, where the recording is the pinned row. What it says is
/// <see cref="RecordingHud"/>'s decision; where it sits is the right end of the dictation panel's
/// edge, so the two never overlap.
/// </remarks>
public sealed partial class RecordingHudWindow : Window
{
    private readonly AppState state;
    private readonly DispatcherTimer clock;

    /// <summary>Gap between the pill and a dictation panel that steps past it, in device pixels.</summary>
    private const double PanelGap = 8;

    /// <summary>
    /// The room the pill takes at the edge, in device pixels, for the dictation panel to step
    /// past; zero while the pill is hidden.
    /// </summary>
    public double EdgeReserve =>
        IsVisible
            ? Math.Round(Pill.Bounds.Height * (Screens.ScreenFromWindow(this)?.Scaling ?? 1)) + PanelGap
            : 0;

    /// <summary>The pill appeared, disappeared or changed height: the panel should place again.</summary>
    public event Action? EdgeChanged;

    public RecordingHudWindow(AppState state, Action openLibrary)
    {
        this.state = state;
        InitializeComponent();

        PointerPressed += (_, args) =>
        {
            args.Handled = true;
            openLibrary();
        };
        PropertyChanged += (_, change) =>
        {
            if (change.Property == ClientSizeProperty && IsVisible) { Place(); EdgeChanged?.Invoke(); }
        };
        // Whole seconds on the clock: twice a second is the most that can show a change.
        clock = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Render());
        clock.Stop();

        state.PropertyChanged += OnStateChanged;
        WorkArea.Changed += OnWorkAreaChanged;
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
            WorkArea.Changed -= OnWorkAreaChanged;
            clock.Stop();
        };
        Render();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppState.RecordingState))
        {
            Dispatcher.UIThread.Post(Render);
        }
        else if (args.PropertyName == nameof(AppState.OverlayPlacement))
        {
            Dispatcher.UIThread.Post(() => { if (IsVisible) { Place(); } });
        }
    }

    private void OnWorkAreaChanged() => Dispatcher.UIThread.Post(() => { if (IsVisible) { Place(); } });

    private void Render()
    {
        RecordingHudContent content = RecordingHud.Content(state.RecordingState, state.LiveDuration);
        if (!content.IsVisible)
        {
            clock.Stop();
            if (IsVisible) { Hide(); EdgeChanged?.Invoke(); }
            return;
        }

        TitleText.Text = content.Title;
        TimeText.Text = content.Time;
        TimeText.IsVisible = content.Time.Length > 0;
        Glyph.Kind = content.IsDotLit ? StateGlyphKind.Listening : StateGlyphKind.Silent;
        AutomationProperties.SetName(Pill, content.AccessibilityLabel);

        if (!clock.IsEnabled) { clock.Start(); }
        if (!IsVisible)
        {
            Show();
            // After the show: until it is laid out the window has no size to place by.
            Place();
            EdgeChanged?.Invoke();
        }
    }

    /// <summary>Same arithmetic as the dictation panel's: the work area is in pixels, the size in DIPs.</summary>
    private void Place()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) { return; }

        PixelRect work = WorkArea.For(screen, Screens.ScreenCount);
        double scale = screen.Scaling;
        OverlayPoint origin = RecordingHud.Origin(
            state.OverlayPlacement,
            OverlayWindow.ShadowFrame(work, Pill.Margin, scale),
            new OverlaySize(Math.Round(Bounds.Width * scale), Math.Round(Bounds.Height * scale)));
        Position = new PixelPoint((int)Math.Round(origin.X), (int)Math.Round(origin.Y));
    }
}
