using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DictationCore;
using DictationCore.Audio;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// One recording in the Library (upstream RecordingDetail.swift, RecordingTransport.swift,
/// TranscriptView.swift): its title (double-click to rename), what kind it is, the transcript
/// with clickable timestamps and the paragraph being heard highlighted, and a transport whose
/// slider sits on the recording's waveform. The recording in progress shows here too
/// (upstream LiveRecordingDetail, CaptureHealthStrip, TranscriptStatusLine): its transcript as it
/// grows, what is wrong with either side, how far behind the words are - and no player.
/// </summary>
/// <remarks>
/// <para>
/// Plays through the Library's one <see cref="RecordingPlayer"/>, owned by the window.
/// <see cref="Show"/> picks the recording (another one pauses what was playing);
/// <see cref="Detach"/> - also run when the view leaves the window - pauses and stops listening.
/// </para>
/// <para>
/// The player has no position events: while it plays, or the slider is dragged, a timer reads
/// <see cref="RecordingPlayer.Position"/> ten times a second. State and player events arrive on
/// background threads and are posted to the UI thread; anything that arrives for a recording no
/// longer shown is dropped.
/// </para>
/// </remarks>
public sealed class RecordingDetailView : UserControl
{
    private static readonly IBrush CurrentRow = new SolidColorBrush(Color.FromArgb(0x24, 0x3E, 0x63, 0xDD));
    private static readonly IBrush AttentionWash = new SolidColorBrush(Color.FromArgb(0x1A, 0xF7, 0x6B, 0x15));

    /// <summary>How far behind the live words may be before the line is worth saying (upstream TranscriptStatusPolicy).</summary>
    private const double BacklogVisibleAfter = 3;

    private readonly AppState state;
    private readonly RecordingPlayer player;
    private readonly TransportScrub scrub = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private readonly TextBlock title = new()
    {
        FontFamily = DictationDetailView.Serif,
        FontSize = 24,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBox titleEditor = new()
    {
        FontFamily = DictationDetailView.Serif,
        FontSize = 24,
        IsVisible = false,
    };

    private readonly TextBlock details = new() { FontSize = 13, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Foreground = StatusBrushes.Attention };
    private readonly StackPanel transcript = new() { MaxWidth = 748, Margin = new Thickness(24, 8) };
    private readonly ScrollViewer scroller = new();
    private readonly Button jumpToLatest = new()
    {
        Content = "↓ Jump to Latest",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 8),
        IsVisible = false,
    };

    private readonly TextBlock placeholderTitle = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock placeholderDetail = new()
    {
        Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 360,
    };

    private readonly StackPanel placeholder = new() { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock importLine = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button retryButton = new() { Content = "Retry", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel statusLine = new() { Margin = new Thickness(24, 4) };
    private readonly StackPanel healthStrip = new() { Spacing = 6, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
    private readonly MenuItem exportTranscript = new() { Header = "Export Transcript…" };
    private readonly MenuItem exportAudio = new() { Header = "Export Audio…" };
    private readonly Button exportButton = new() { Content = "Export…", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 4, 0, 0) };
    private readonly Grid transportBar = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(24, 8) };
    private readonly Button playButton = new() { Width = 40, Height = 36, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock positionText = new() { MinWidth = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock durationText = new() { MinWidth = 48, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider slider = new() { Minimum = 0, Maximum = 0.01, VerticalAlignment = VerticalAlignment.Center };
    private readonly PeakWaveform waveform = new() { Height = 32, VerticalAlignment = VerticalAlignment.Center };

    private MeetingRecordingMetadata? recording;
    private IReadOnlyList<MeetingUtterance>? renderedTranscript;
    private readonly Dictionary<Guid, Border> rows = [];
    private Guid? highlighted;
    private TranscriptFollowState follow = new();
    private bool following;
    private bool audioMissing;
    private string? audioPath;
    private bool renaming;
    private bool attached;
    private bool updatingSlider;

    /// <summary>The shown recording was the one in progress when last drawn: its end is noticed once.</summary>
    private bool wasLive;

    public RecordingDetailView(AppState state, RecordingPlayer player)
    {
        this.state = state;
        this.player = player;
        Focusable = true;

        // Header: title, or its editor while renaming; then what it is.
        title.DoubleTapped += (_, _) => BeginRename();
        ToolTip.SetTip(title, "Double-click to rename");
        titleEditor.KeyDown += OnEditorKey;
        titleEditor.LostFocus += (_, _) => CommitRename();
        AutomationProperties.SetName(titleEditor, "Recording name");
        exportTranscript.Click += (_, _) =>
        {
            if (recording is { } shown) { _ = RecordingExport.Transcript(this, state, shown); }
        };
        exportAudio.Click += (_, _) =>
        {
            if (recording is { } shown) { _ = RecordingExport.Audio(this, state, shown); }
        };
        exportButton.Flyout = new MenuFlyout { Items = { exportTranscript, exportAudio } };
        AutomationProperties.SetName(exportButton, "Export");
        var naming = new Panel();
        naming.Children.Add(title);
        naming.Children.Add(titleEditor);
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(naming);
        Grid.SetColumn(exportButton, 1);
        titleRow.Children.Add(exportButton);
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(24, 20, 24, 12) };
        header.Children.Add(titleRow);
        header.Children.Add(details);
        header.Children.Add(note);
        header.Children.Add(healthStrip);

        // The transcript, with its placeholder and the way back to the latest words over it.
        scroller.Content = transcript;
        scroller.ScrollChanged += OnScrolled;
        jumpToLatest.Click += (_, _) =>
        {
            follow.ReturnToLatest();
            jumpToLatest.IsVisible = false;
            scroller.ScrollToEnd();
        };
        placeholderDetail.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.Children.Add(placeholderTitle);
        placeholder.Children.Add(placeholderDetail);
        var body = new Panel();
        body.Children.Add(scroller);
        body.Children.Add(placeholder);
        body.Children.Add(jumpToLatest);

        // The transport: play/pause, where, the slider on its waveform, how long.
        playButton.Click += (_, _) =>
        {
            player.Toggle();
            UpdatePlayhead();
        };
        slider.AddHandler(PointerPressedEvent, OnSliderPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
        slider.ValueChanged += OnSliderMoved;
        AutomationProperties.SetName(slider, "Position");
        var track = new Panel();
        track.Children.Add(waveform);
        track.Children.Add(slider);
        Grid transport = transportBar;
        transport.Children.Add(playButton);
        Grid.SetColumn(positionText, 1);
        transport.Children.Add(positionText);
        track.Margin = new Thickness(12, 0);
        Grid.SetColumn(track, 2);
        transport.Children.Add(track);
        Grid.SetColumn(durationText, 3);
        transport.Children.Add(durationText);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        DockPanel.SetDock(transport, Dock.Bottom);
        layout.Children.Add(transport);
        retryButton.Click += (_, _) => state.ResumeTranscription();
        DockPanel.SetDock(retryButton, Dock.Right);
        statusLine.Children.Add(retryButton);
        statusLine.Children.Add(importLine);
        DockPanel.SetDock(statusLine, Dock.Bottom);
        layout.Children.Add(statusLine);
        layout.Children.Add(body);
        Content = layout;

        ticker.Tick += (_, _) => UpdatePlayhead();
        KeyDown += OnKey;
    }

    /// <summary>
    /// Show this recording. Another one than before pauses what was playing and loads this one;
    /// the same one (renamed, transcribed further) only redraws.
    /// </summary>
    public void Show(MeetingRecordingMetadata shown)
    {
        bool another = recording?.Id != shown.Id;
        recording = shown;
        if (another)
        {
            player.Pause();
            scrub.Cancel();
            renaming = false;
            follow = new TranscriptFollowState();
            renderedTranscript = null;
            highlighted = null;
            waveform.Channels = [];
            waveform.Progress = 0;
            wasLive = state.LiveRecordingId == shown.Id;
            LoadPeaks(shown.Id);
        }

        LoadAudio(shown.Id);
        Render();
    }

    /// <summary>Start renaming, as a double-click on the title does (the window's Return and context menu).</summary>
    public void BeginRename()
    {
        if (recording is null || IsLive) { return; }
        renaming = true;
        titleEditor.Text = recording.Title ?? string.Empty;
        titleEditor.Watermark = RecordingsPlaceholder.DefaultTitle(recording.StartedAt);
        RenderHeader();
        titleEditor.Focus();
        titleEditor.SelectAll();
    }

    /// <summary>Leaving the recording: playback pauses, the view stops listening.</summary>
    public void Detach()
    {
        player.Pause();
        scrub.Cancel();
        ticker.Stop();
        if (!attached) { return; }
        attached = false;
        state.PropertyChanged -= OnStateChanged;
        state.TranscriptChanged -= OnTranscriptChanged;
        player.Ended -= OnEnded;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (attached) { return; }
        attached = true;
        state.PropertyChanged += OnStateChanged;
        state.TranscriptChanged += OnTranscriptChanged;
        player.Ended += OnEnded;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    // MARK: - Events from elsewhere

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        // The meters live in the window's record bar; ten redraws a second here would be waste.
        if (recording is null || args.PropertyName == nameof(AppState.LiveLevels)) { return; }
        // Renamed, or transcribed further: the metadata as listed now. Gone: the window decides.
        if (state.Recordings.FirstOrDefault(listed => listed.Id == recording.Id) is { } current)
        {
            recording = current;
        }

        RefreshAudio();
        Render();
    });

    private void OnTranscriptChanged(Guid id) => Dispatcher.UIThread.Post(() =>
    {
        if (recording?.Id != id) { return; }
        RefreshAudio();
        Render();
    });

    /// <summary>
    /// A finished import swaps its WAV for the compact archive: load the file that is there now.
    /// What is playing carries on from its open file and is swapped once it stops.
    /// </summary>
    private void RefreshAudio()
    {
        if (recording is not { } current || state.RecordingAudioPath(current.Id) == audioPath) { return; }
        if (player.LoadedId == current.Id && player.IsPlaying) { return; }
        LoadAudio(current.Id);
    }

    private void OnEnded(object? sender, Guid id) => Dispatcher.UIThread.Post(() =>
    {
        if (recording?.Id == id) { UpdatePlayhead(); }
    });

    // MARK: - Loading

    private void LoadAudio(Guid id)
    {
        string? path = state.RecordingAudioPath(id);
        audioPath = path;
        audioMissing = path is null;
        if (path is null) { return; }
        // Opening an m4a probes it with ffprobe: off the UI thread.
        _ = Task.Run(() => player.Load(id, path)).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (recording?.Id == id) { Render(); }
            }),
            TaskScheduler.Default);
    }

    private void LoadPeaks(Guid id)
    {
        if (state.RecordingPeaksPath(id) is not { } path) { return; }
        _ = Task.Run(() =>
        {
            try
            {
                return PeakFile.Read(path).Channels;
            }
            catch (PeakFile.Failure)
            {
                return [];
            }
        }).ContinueWith(
            read => Dispatcher.UIThread.Post(() =>
            {
                if (recording?.Id == id) { waveform.Channels = read.Result; }
            }),
            TaskScheduler.Default);
    }

    // MARK: - Rendering

    private bool IsAudioLoaded => recording is { } current && player.LoadedId == current.Id;

    /// <summary>The shown recording is the one in progress: no player, no export, no rename yet.</summary>
    private bool IsLive => recording is { } current && state.LiveRecordingId == current.Id;

    private void Render()
    {
        if (recording is null) { return; }
        bool live = IsLive;
        // Just filed: the waveform is there to read now (the audio follows through RefreshAudio).
        if (wasLive && !live) { LoadPeaks(recording.Id); }
        wasLive = live;
        transportBar.IsVisible = !live;
        RenderHeader();
        RenderTranscript();
        UpdatePlayhead();
    }

    private void RenderHeader()
    {
        if (recording is null) { return; }
        bool live = IsLive;
        title.Text = recording.Title ?? RecordingsPlaceholder.DefaultTitle(recording.StartedAt);
        title.IsVisible = !renaming;
        titleEditor.IsVisible = renaming;
        DateTimeOffset started = recording.StartedAt.ToLocalTime();
        string length = !live
            ? RecordingTime.Brief(TimeSpan.FromSeconds(recording.Duration))
            : (state.RecordingState == MeetingRecordingState.Paused ? "Paused — " : "Recording — ") + RecordingTime.Clock(state.LiveDuration);
        details.Text = string.Join(
            " · ",
            RecordingsPlaceholder.Kind(LibraryRecording.KindOf(recording)),
            started.ToString("D", CultureInfo.CurrentCulture) + ", " + started.ToString("t", CultureInfo.CurrentCulture),
            length);
        string? warning = live ? null : RecordingsPlaceholder.EndNote(recording.EndReason) ?? RecordingsPlaceholder.DegradedNote(recording);
        note.Text = warning;
        note.IsVisible = warning is not null;

        exportButton.IsVisible = !live;
        exportTranscript.IsEnabled = state.Transcript(recording.Id).Count > 0;
        exportAudio.IsEnabled = audioPath is not null;
        RenderHealth(live);
    }

    /// <summary>
    /// While recording, one card per side that is not arriving as it should (upstream
    /// CaptureHealthStrip): orange when the recording is missing that side, grey when it is only
    /// something this computer cannot do.
    /// </summary>
    private void RenderHealth(bool live)
    {
        healthStrip.Children.Clear();
        if (live)
        {
            MicrophoneHealth microphone = state.LiveMicrophoneHealth;
            CaptureHealth others = state.LiveCaptureHealth;
            if (microphone.Title is { } microphoneTitle)
            {
                healthStrip.Children.Add(HealthCard(microphoneTitle, microphone.Detail, microphone.MarksRecordingDegraded));
            }

            if (others.Title is { } othersTitle)
            {
                healthStrip.Children.Add(HealthCard(othersTitle, others.Detail, others.MarksRecordingDegraded));
            }
        }

        healthStrip.IsVisible = healthStrip.Children.Count > 0;
    }

    private static Border HealthCard(string heading, string? detail, bool degraded)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = (degraded ? "⚠ " : string.Empty) + heading,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        }

        var card = new Border
        {
            Child = text,
            BorderBrush = degraded ? StatusBrushes.Attention : StatusBrushes.Neutral,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
        };
        if (degraded) { card.Background = AttentionWash; }
        AutomationProperties.SetName(card, detail is null ? heading : heading + ". " + detail);
        return card;
    }

    /// <summary>
    /// One honest line under a live transcript about how far behind it is, and nothing when it
    /// is not worth saying (upstream TranscriptStatusLine). Orange only for a stopped
    /// transcription: the audio is safe either way.
    /// </summary>
    private (string Text, bool Paused)? TranscriptStatus(Guid id)
    {
        if (!IsLive && state.TranscribingRecordingId != id) { return null; }
        if (state.IsTranscriptionPaused) { return ("Transcription paused.", true); }
        if (!state.IsEngineReady) { return ("Waiting for the speech model.", false); }
        if (state.DictationStateValue != DictationState.Idle) { return ("Transcription paused while you dictate.", false); }
        double backlog = state.TranscriptBacklogSeconds;
        return backlog >= BacklogVisibleAfter
            ? ($"Transcribing — about {Math.Round(backlog).ToString(CultureInfo.CurrentCulture)} seconds behind.", false)
            : null;
    }

    private void RenderTranscript()
    {
        if (recording is not { } current) { return; }
        IReadOnlyList<MeetingUtterance> utterances = state.Transcript(current.Id);
        RecordingImportActivity activity = state.ImportActivity;
        bool live = IsLive;
        following = activity.Transcribing == current.Id || live || state.TranscribingRecordingId == current.Id;

        bool isDictating = state.DictationStateValue != DictationState.Idle;
        string? line = activity.Transcribing == current.Id || utterances.Count > 0
            ? RecordingsPlaceholder.ImportLine(current.Id, activity, isDictating)
            : null;
        (string Text, bool Paused)? status = line is null ? TranscriptStatus(current.Id) : null;
        importLine.Text = line ?? status?.Text;
        importLine.Foreground = status is { Paused: true } ? StatusBrushes.Attention : Brushes.Gray;
        retryButton.IsVisible = status is { Paused: true };
        statusLine.IsVisible = importLine.Text is not null;

        if (utterances.Count == 0)
        {
            RecordingsPlaceholder shown = live
                ? RecordingsPlaceholder.Listening
                : audioMissing || (!IsAudioLoaded && player.FailedToLoad)
                ? RecordingsPlaceholder.AudioMissing
                : RecordingsPlaceholder.Importing(current.Id, activity)
                    ?? RecordingsPlaceholder.Transcript(current.TranscriptionState);
            placeholderTitle.Text = shown.Title;
            placeholderDetail.Text = shown.Detail;
            placeholder.IsVisible = true;
            scroller.IsVisible = false;
            jumpToLatest.IsVisible = false;
            transcript.Children.Clear();
            rows.Clear();
            renderedTranscript = utterances;
            return;
        }

        placeholder.IsVisible = false;
        scroller.IsVisible = true;
        jumpToLatest.IsVisible = following && !follow.FollowsLatest;
        if (ReferenceEquals(utterances, renderedTranscript)) { return; }
        renderedTranscript = utterances;

        transcript.Children.Clear();
        rows.Clear();
        highlighted = null;
        // Who speaks in an imported file is not something the app knows: no headings there.
        bool speakers = !current.IsImported;
        foreach (TranscriptSection section in TranscriptSection.Make(utterances))
        {
            if (speakers) { transcript.Children.Add(SpeakerHeading(section.Channel)); }
            foreach (MeetingUtterance utterance in section.Utterances)
            {
                Border row = Paragraph(utterance, speakers);
                rows[utterance.Id] = row;
                transcript.Children.Add(row);
            }
        }

        if (following && follow.FollowsLatest)
        {
            // After the new paragraphs are laid out.
            Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private static Control SpeakerHeading(MeetingChannel channel)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 12, 6, 4) };
        heading.Children.Add(new Border
        {
            Width = 3,
            Height = 12,
            CornerRadius = new CornerRadius(1),
            Background = channel == MeetingChannel.Microphone ? StatusBrushes.Processing : Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        });
        heading.Children.Add(new TextBlock
        {
            Text = MeetingTranscriptFormatter.Name(channel),
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Gray,
        });
        return heading;
    }

    private Border Paragraph(MeetingUtterance utterance, bool speakers)
    {
        var stamp = new Button
        {
            Content = RecordingTime.Clock(TimeSpan.FromSeconds(utterance.Start)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2),
            FontSize = 12,
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(stamp, "Play from " + RecordingTime.Clock(TimeSpan.FromSeconds(utterance.Start)));
        AutomationProperties.SetName(stamp, "Play from " + RecordingTime.Spoken(TimeSpan.FromSeconds(utterance.Start)));
        stamp.Click += (_, _) => PlayFrom(utterance.Start);

        Control text = utterance.IsFailed
            ? new TextBlock
            {
                Text = "Couldn't transcribe this part",
                FontStyle = FontStyle.Italic,
                Foreground = StatusBrushes.Attention,
                FontSize = 15,
            }
            : new SelectableTextBlock
            {
                Text = utterance.Text,
                FontSize = 15,
                LineHeight = 22,
                TextWrapping = TextWrapping.Wrap,
            };
        string said = utterance.IsFailed ? "Couldn't transcribe this part" : utterance.Text;
        AutomationProperties.SetName(text, speakers ? MeetingTranscriptFormatter.Name(utterance.Channel) + ": " + said : said);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*") };
        grid.Children.Add(stamp);
        text.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(6),
        };
    }

    /// <summary>The playhead, ten times a second while something moves: slider, clocks, waveform, paragraph.</summary>
    private void UpdatePlayhead()
    {
        bool loaded = IsAudioLoaded;
        bool playing = loaded && player.IsPlaying;
        double duration = loaded ? player.Duration.TotalSeconds : recording?.Duration ?? 0;
        double position = scrub.Displayed(loaded ? player.Position.TotalSeconds : 0);

        updatingSlider = true;
        try
        {
            slider.Maximum = Math.Max(duration, 0.01);
            slider.Value = Math.Clamp(position, 0, slider.Maximum);
        }
        finally
        {
            updatingSlider = false;
        }

        slider.IsEnabled = loaded && duration > 0;
        playButton.IsEnabled = loaded && duration > 0;
        playButton.Content = playing ? "❚❚" : "▶";
        AutomationProperties.SetName(playButton, playing ? "Pause" : "Play");
        positionText.Text = RecordingTime.Clock(TimeSpan.FromSeconds(position));
        durationText.Text = RecordingTime.Clock(TimeSpan.FromSeconds(duration));
        waveform.Progress = duration > 0 ? position / duration : 0;
        Highlight(playing || position > 0 ? TranscriptPlayback.Current(renderedTranscript ?? [], position) : null);

        bool moving = playing || scrub.IsScrubbing;
        if (moving && !ticker.IsEnabled) { ticker.Start(); }
        else if (!moving && ticker.IsEnabled) { ticker.Stop(); }
    }

    private void Highlight(Guid? current)
    {
        if (current == highlighted) { return; }
        if (highlighted is { } previous && rows.TryGetValue(previous, out Border? old)) { old.Background = null; }
        if (current is { } next && rows.TryGetValue(next, out Border? row)) { row.Background = CurrentRow; }
        highlighted = current;
    }

    // MARK: - Playing

    private void PlayFrom(double seconds)
    {
        if (!IsAudioLoaded) { return; }
        player.Seek(TimeSpan.FromSeconds(seconds));
        player.Play();
        UpdatePlayhead();
    }

    private void OnSliderPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsAudioLoaded) { return; }
        if (scrub.Begin(player.IsPlaying, slider.Value)) { player.Pause(); }
        UpdatePlayhead();
    }

    private void OnSliderMoved(object? sender, RangeBaseValueChangedEventArgs args)
    {
        if (updatingSlider || !IsAudioLoaded) { return; }
        if (scrub.IsScrubbing)
        {
            scrub.Move(args.NewValue);
            UpdatePlayhead();
            return;
        }

        // The keyboard: a step is a seek.
        player.Seek(TimeSpan.FromSeconds(args.NewValue));
        UpdatePlayhead();
    }

    private void EndScrub()
    {
        if (scrub.End() is not { } end) { return; }
        player.Seek(TimeSpan.FromSeconds(end.SeekTo));
        if (end.Resume) { player.Play(); }
        UpdatePlayhead();
    }

    private void OnKey(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Space || renaming || args.Source is TextBox || !IsAudioLoaded) { return; }
        player.Toggle();
        UpdatePlayhead();
        args.Handled = true;
    }

    // MARK: - Following the latest words

    private void OnScrolled(object? sender, ScrollChangedEventArgs args)
    {
        // Only the person moves the offset by hand; new words grow the extent, not the offset.
        if (!following || args.OffsetDelta.Y == 0) { return; }
        follow.UserScrolled(scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height);
        jumpToLatest.IsVisible = !follow.FollowsLatest;
    }

    // MARK: - Renaming

    private void OnEditorKey(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            CommitRename();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            renaming = false;
            RenderHeader();
            Focus();
            args.Handled = true;
        }
    }

    private void CommitRename()
    {
        if (!renaming || recording is null) { return; }
        renaming = false;
        state.RenameRecording(recording.Id, titleEditor.Text);
        RenderHeader();
        Focus();
    }
}
