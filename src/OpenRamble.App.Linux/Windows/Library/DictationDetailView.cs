using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenRamble.App.Linux.Controls;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// One dictation in the Library (upstream DictationDetail.swift): its words in the reading face,
/// when it was said, Play/Stop, Recognize again, Keep.
/// </summary>
/// <remarks>
/// Plays through the Library's one <see cref="RecordingPlayer"/>, owned by the window, so a
/// dictation and a recording never sound at once. <see cref="Show"/> picks the entry;
/// <see cref="Detach"/> (also run when the view leaves the window) stops what it was playing.
/// </remarks>
public sealed class DictationDetailView : UserControl
{
    private readonly AppState state;
    private readonly RecordingPlayer player;
    private readonly TextBlock date = new() { Classes = { "detail" } };
    private readonly SelectableTextBlock words = new()
    {
        FontFamily = LibraryLook.Serif,
        FontSize = 26,
        LineHeight = 36,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock nothing = new()
    {
        Classes = { "words26" },
        Text = "Nothing recognized",
    };

    private readonly Button play = new() { Classes = { "quiet" } };
    private readonly LucideIcon playIcon;
    private readonly TextBlock playLabel;
    private readonly Button again = new() { Classes = { "quiet" } };
    private readonly ToggleButton keep = new() { Classes = { "quiet" } };
    private readonly LucideIcon keepIcon;
    private readonly TextBlock gone = new() { Classes = { "detail" }, Text = "Recording no longer on disk" };
    private HistoryEntry? entry;
    private bool attached;
    private bool updating;

    public DictationDetailView(AppState state, RecordingPlayer player)
    {
        this.state = state;
        this.player = player;
        ThemeTokens.Bind(words, TextBlock.ForegroundProperty, ThemeTokens.Label);
        ThemeTokens.Bind(nothing, TextBlock.ForegroundProperty, ThemeTokens.Label2);
        play.Content = LibraryLook.Labelled("IconPlay", "Play", out playIcon, out playLabel);
        again.Content = LibraryLook.Labelled("IconRotateCcw", "Recognize again", out _, out _);
        keep.Content = LibraryLook.Labelled("IconStar", "Keep", out keepIcon, out _);

        play.Click += (_, _) => TogglePlayback();
        again.Click += (_, _) =>
        {
            if (entry is not null) { state.RetranscribeHistoryEntry(entry); }
        };
        ToolTip.SetTip(again, "Recognize this recording again");
        AutomationProperties.SetName(again, "Recognize this recording again");
        keep.IsCheckedChanged += (_, _) =>
        {
            if (updating || entry is null) { return; }
            state.SetHistoryEntryKept(keep.IsChecked == true, entry);
        };

        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(keep, Dock.Right);
        buttons.Children.Add(keep);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        left.Children.Add(play);
        left.Children.Add(again);
        left.Children.Add(gone);
        gone.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(left);

        var column = new StackPanel { Spacing = 24, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        column.Children.Add(date);
        column.Children.Add(words);
        column.Children.Add(nothing);
        column.Children.Add(buttons);
        Content = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(44, 36), Child = column },
        };
    }

    /// <summary>Show this dictation; one that was playing stops.</summary>
    public void Show(HistoryEntry shown)
    {
        if (entry is { } previous && previous.Id != shown.Id)
        {
            Interlocked.Increment(ref playRequest);
            StopIfPlaying(previous.Id);
        }

        entry = shown;
        Render();
    }

    /// <summary>Leaving the dictation: its playback stops and the view stops listening.</summary>
    public void Detach()
    {
        Interlocked.Increment(ref playRequest);
        if (entry is { } current) { StopIfPlaying(current.Id); }
        Unsubscribe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (attached) { return; }
        attached = true;
        state.PropertyChanged += OnStateChanged;
        player.Ended += OnEnded;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    private void Unsubscribe()
    {
        if (!attached) { return; }
        attached = false;
        state.PropertyChanged -= OnStateChanged;
        player.Ended -= OnEnded;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) => Dispatcher.UIThread.Post(Render);

    private void OnEnded(object? sender, Guid id) => Dispatcher.UIThread.Post(() =>
    {
        if (entry?.Id == id) { Render(); }
    });

    /// <summary>Bumped by every play request, a switch to another dictation and a detach.</summary>
    private long playRequest;

    private bool IsPlaying => entry is { } current && player.LoadedId == current.Id && player.IsPlaying;

    private void TogglePlayback()
    {
        if (entry is not { } current || state.HistoryAudioPath(current) is not { } audio) { return; }
        if (IsPlaying)
        {
            StopIfPlaying(current.Id);
            Render();
            return;
        }

        // Opening the file may probe it with ffprobe: off the UI thread. Another dictation shown,
        // or the view gone, meanwhile: nothing plays, since nothing would be left to stop it.
        long request = Interlocked.Increment(ref playRequest);
        _ = Task.Run(() =>
        {
            player.Load(current.Id, audio);
            if (player.LoadedId != current.Id || Interlocked.Read(ref playRequest) != request) { return; }
            player.Seek(TimeSpan.Zero);
            player.Play();
            // Left between the check and the start: stop what nobody can stop now.
            if (Interlocked.Read(ref playRequest) != request) { StopIfPlaying(current.Id); }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(Render), TaskScheduler.Default);
    }

    private void StopIfPlaying(Guid id)
    {
        if (player.LoadedId != id) { return; }
        player.Pause();
        player.Seek(TimeSpan.Zero);
    }

    private void Render()
    {
        if (entry is null) { return; }
        // The entry as it is now: recognised again, or kept, since it was picked.
        HistoryEntry current = state.History.FirstOrDefault(listed => listed.Id == entry.Id) ?? entry;
        entry = current;

        date.Text = current.Date.ToLocalTime().ToString("D", CultureInfo.CurrentCulture)
            + ", " + current.Date.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        bool hasWords = current.Text.Length > 0;
        words.Text = current.Text;
        words.IsVisible = hasWords;
        nothing.IsVisible = !hasWords;

        bool hasAudio = state.HistoryAudioPath(current) is not null;
        bool playing = IsPlaying;
        play.IsVisible = hasAudio;
        again.IsVisible = hasAudio;
        gone.IsVisible = !hasAudio;
        playIcon.Data = LibraryLook.Geometry(playing ? "IconSquare" : "IconPlay");
        playLabel.Text = playing ? "Stop" : "Play";
        AutomationProperties.SetName(play, playing ? "Stop playback" : "Play this recording");
        again.IsEnabled = !state.IsRetranscribing && state.IsEngineReady;

        updating = true;
        try
        {
            keep.IsChecked = current.IsKept;
        }
        finally
        {
            updating = false;
        }

        // The star is the only thing that changes colour: a kept one is orange.
        if (current.IsKept) { keepIcon.Foreground = StatusBrushes.Attention; }
        else { keepIcon.ClearValue(LucideIcon.ForegroundProperty); }
        ToolTip.SetTip(keep, current.IsKept ? "Stop keeping this one" : "Keep this one");
        AutomationProperties.SetName(keep, current.IsKept ? "Kept. Stop keeping" : "Keep this dictation");
    }
}
