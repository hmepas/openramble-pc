using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OpenRamble.App.Linux.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DictationCore;
using DictationCore.Audio;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The Library: every dictation and recording in one list by day, the selected one beside it
/// (upstream RecordingsWindow.swift and RecordingsList.swift).
/// </summary>
/// <remarks>
/// <para>
/// Rows are built by hand, as the Settings history rows were: day headers are rows that cannot be
/// selected, and each row carries its own context menu. The list is rebuilt only when what it is
/// built from changes - the history, the recordings, the scope or the search; an import's
/// progress rewrites the one status line it touches.
/// </para>
/// <para>
/// The detail pane is whatever <see cref="CreateDetail"/> makes for the selection; the window owns
/// the selection rules and the one player the panes share, and nothing else of the detail.
/// </para>
/// </remarks>
public sealed partial class LibraryWindow : Window, IDictationSink
{
    /// <summary>How many recent peaks a record-bar meter draws.</summary>
    private const int MeterBars = 24;

    /// <summary>What the file picker offers (upstream: any audio or movie type).</summary>
    private static readonly string[] ImportPatterns =
    [
        "*.m4a", "*.mp3", "*.wav", "*.aac", "*.flac", "*.ogg", "*.oga", "*.opus", "*.wma", "*.aif", "*.aiff", "*.caf", "*.amr", "*.webm",
        "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.avi", "*.wmv", "*.mpg", "*.mpeg", "*.3gp",
    ];

    private static readonly LibraryScope[] Scopes = [LibraryScope.All, LibraryScope.Dictations, LibraryScope.Recordings];

    private readonly AppState state;

    /// <summary>The one player every detail pane plays through; released when the window closes.</summary>
    private readonly RecordingPlayer player = new();

    /// <summary>The items the list shows, in its order; headers excluded.</summary>
    private List<LibraryItem> listed = [];

    /// <summary>Each recording row's status line, rewritten in place as an import advances.</summary>
    private readonly Dictionary<Guid, TextBlock> statusLines = [];

    /// <summary>What the list was last built from, so a property change that touched none of it is free.</summary>
    private (IReadOnlyList<HistoryEntry> History, IReadOnlyList<MeetingRecordingMetadata> Recordings, LibraryScope Scope, string Query, Guid? Live)? rendered;

    /// <summary>The pinned live row's state and time, rewritten in place once a second.</summary>
    private (ListBoxItem Row, Ellipse Dot, TextBlock State, TextBlock Time)? liveRow;

    /// <summary>Each side's recent peaks, oldest first, for the record bar's meters.</summary>
    private readonly Queue<float> youLevels = new(), othersLevels = new();

    /// <summary>The ids selected, kept across rebuilds of the list.</summary>
    private HashSet<Guid> selection = [];

    /// <summary>The item the detail pane was made for: a new pane only when this changes.</summary>
    private (Guid Id, bool IsRecording)? detailFor;

    /// <summary>Guards the list's SelectionChanged against what a rebuild does to it.</summary>
    private bool updating;

    /// <summary>Recordings are on their way to the Trash (off the UI thread); Delete waits.</summary>
    private bool trashing;

    public LibraryWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();
        Icon = TrayIconArt.WindowMark();

        ScopePicker.ItemsSource = new[] { "All", "Dictations", "Recordings" };
        ScopePicker.SelectedIndex = 0;
        ScopePicker.SelectionChanged += (_, _) => RenderList();
        SearchBox.TextChanged += (_, _) => RenderList();
        ItemList.SelectionChanged += OnListSelectionChanged;
        ItemList.KeyDown += OnListKeyDown;
        MicOnlyToggle.IsCheckedChanged += (_, _) => RenderRecordBar();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);

        state.PropertyChanged += OnStateChanged;
        state.TranscriptChanged += OnTranscriptChanged;
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
            state.TranscriptChanged -= OnTranscriptChanged;
            DetachDetail();
            player.Dispose();
        };

        state.ReloadRecordings();
        RenderList();
        // What is happening now before what merely came last: a recording running, then the file
        // whose words are arriving.
        Guid? first = state.LiveRecordingId
            ?? (state.ImportActivity.Transcribing is { } transcribing && listed.Any(item => item.Id == transcribing)
                ? transcribing
                : listed.FirstOrDefault()?.Id);
        if (first is { } id) { Select([id]); }
        RenderToolbar();
        RenderRecordBar();
    }

    /// <summary>Dictation over the Library goes into the search box when it has the caret.</summary>
    public bool TryInsert(string text)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box) { return false; }
        if (text == "\n" && !box.AcceptsReturn) { return true; }

        DictationSinkTextBox.Insert(box, text);
        return true;
    }

    // MARK: - Detail seam

    /// <summary>
    /// The detail pane for one selected item, or the window's placeholder when there is none (no
    /// selection, several, or nothing listed).
    /// </summary>
    /// <remarks>
    /// Made again only when the selected item changes, not when its data does: a pane follows its
    /// item through <see cref="AppState"/> itself, so a player keeps playing through a rename.
    /// </remarks>
    private Control CreateDetail(LibraryItem? selected) => selected switch
    {
        LibraryItem.Dictation dictation => Shown(new DictationDetailView(state, player), view => view.Show(dictation.Entry)),
        LibraryItem.Recording recording when Metadata(recording.Value) is { } metadata =>
            Shown(new RecordingDetailView(state, player), view => view.Show(metadata)),
        _ => PlaceholderView(Placeholder()),
    };

    private static T Shown<T>(T view, Action<T> show)
    {
        show(view);
        return view;
    }

    private MeetingRecordingMetadata? Metadata(ILibraryRecording recording) =>
        recording is LibraryRecording { Metadata: { } metadata }
            ? metadata
            : state.Recordings.FirstOrDefault(stored => stored.Id == recording.Id);

    /// <summary>The pane being replaced lets go of the player and of the state's events.</summary>
    private void DetachDetail()
    {
        switch (Detail.Content)
        {
            case DictationDetailView dictation:
                dictation.Detach();
                break;
            case RecordingDetailView recording:
                recording.Detach();
                break;
        }
    }

    private RecordingsPlaceholder Placeholder()
    {
        if (state.History.Count == 0 && state.Recordings.Count == 0) { return RecordingsPlaceholder.EmptyLibrary; }
        return listed.Count == 0 ? RecordingsPlaceholder.NothingFound : RecordingsPlaceholder.NothingSelected;
    }

    private static Control PlaceholderView(RecordingsPlaceholder placeholder)
    {
        LucideIcon inbox = LibraryLook.Icon("IconInbox", 28);
        inbox.HorizontalAlignment = HorizontalAlignment.Center;
        ThemeTokens.Bind(inbox, LucideIcon.ForegroundProperty, ThemeTokens.Label3);
        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 380,
            Spacing = 10,
            Children =
            {
                inbox,
                new TextBlock
                {
                    Classes = { "display26" },
                    Text = placeholder.Title,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Classes = { "body" },
                    Text = placeholder.Detail,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };
    }

    // MARK: - Live updates

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(() =>
        {
            switch (args.PropertyName)
            {
                case nameof(AppState.LastFinishedRecordingId):
                    // The file just brought in is what the person came to see.
                    state.ReloadRecordings();
                    RenderList();
                    if (state.LastFinishedRecordingId is { } id && listed.Any(item => item.Id == id)) { Select([id]); }
                    break;
                case nameof(AppState.ImportActivity):
                case nameof(AppState.DictationStateValue):
                    RenderStatusLines();
                    break;
                case nameof(AppState.LiveLevels):
                    // Ten times a second: the meters and nothing else.
                    PushLevels();
                    RenderMeters();
                    return;
                case nameof(AppState.LiveDuration):
                case nameof(AppState.RecordingState):
                case nameof(AppState.LiveCaptureHealth):
                case nameof(AppState.LiveMicrophoneHealth):
                    RenderRecordBar();
                    break;
                case nameof(AppState.LiveRecordingId):
                    // A recording that starts is what the person is looking at now.
                    RenderList();
                    RenderRecordBar();
                    if (state.LiveRecordingId is { } live) { Select([live]); }
                    break;
                default:
                    RenderList();
                    break;
            }

            RenderToolbar();
        });

    /// <summary>A transcript grew: the search reads transcripts, and Copy depends on there being words.</summary>
    private void OnTranscriptChanged(Guid id) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Progress must not rebuild (and scroll) the list: only a change in what matches does.
            if (SearchBox.Text?.Trim().Length > 0) { RenderList(force: true, onlyIfMatchesChanged: true); }
            RenderToolbar();
        });

    // MARK: - List

    private LibraryScope Scope => ScopePicker.SelectedIndex is >= 0 and < 3 ? Scopes[ScopePicker.SelectedIndex] : LibraryScope.All;

    private void RenderList(bool force = false, bool onlyIfMatchesChanged = false)
    {
        IReadOnlyList<HistoryEntry> history = state.History;
        IReadOnlyList<MeetingRecordingMetadata> recordings = state.Recordings;
        string query = SearchBox.Text ?? string.Empty;
        MeetingRecordingMetadata? live = state.LiveRecording;
        var inputs = (history, recordings, Scope, query, Live: live?.Id);
        if (!force && rendered is { } last
            && ReferenceEquals(last.History, history)
            && ReferenceEquals(last.Recordings, recordings)
            && last.Scope == inputs.Scope
            && last.Query == query
            && last.Live == inputs.Live)
        {
            return;
        }

        // A new search or scope starts the list from its top; only the rest may keep the selection in view.
        bool refiltered = rendered is { } previous && (previous.Query != query || previous.Scope != inputs.Scope);
        rendered = inputs;
        IReadOnlyList<LibraryDay> days = state.LibraryDays(Scope, query);
        // The recording in progress is pinned above everything, whatever the scope or search
        // (upstream RecordingsList.swift).
        LibraryItem? pinned = live is null ? null : new LibraryItem.Recording(new LibraryRecording(live));
        if (onlyIfMatchesChanged
            && (pinned is null ? [] : new[] { pinned.Id })
                .Concat(days.SelectMany(day => day.Items).Select(item => item.Id))
                .SequenceEqual(listed.Select(item => item.Id)))
        {
            RenderStatusLines();
            return;
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        updating = true;
        try
        {
            var rows = new List<ListBoxItem>();
            listed = [];
            statusLines.Clear();
            liveRow = null;
            if (pinned is not null)
            {
                listed.Add(pinned);
                rows.Add(LiveRow(pinned));
            }

            foreach (LibraryDay day in days)
            {
                rows.Add(DayRow(day.Title(today)));
                foreach (LibraryItem item in day.Items)
                {
                    listed.Add(item);
                    rows.Add(item switch
                    {
                        LibraryItem.Dictation dictation => DictationRow(dictation),
                        LibraryItem.Recording recording => RecordingRow(recording, day.NeedsSeconds(recording.Value)),
                        _ => throw new InvalidOperationException(),
                    });
                }
            }

            ItemList.ItemsSource = rows;
            // What is selected has to be something the list still shows; nothing left - the first.
            var available = listed.Select(item => item.Id).ToHashSet();
            selection.IntersectWith(available);
            if (selection.Count == 0 && listed.Count > 0) { selection = [listed[0].Id]; }
            ApplySelectionToList(scroll: !onlyIfMatchesChanged && !refiltered);
            if (refiltered) { ItemList.Scroll?.Offset = default; }
        }
        finally
        {
            updating = false;
        }

        RenderStatusLines();
        RenderDetail();
        RenderToolbar();
    }

    private static ListBoxItem DayRow(string title) => new()
    {
        Classes = { "dayHeader" },
        IsEnabled = false,
        Focusable = false,
        Content = new TextBlock { Classes = { "small" }, Text = title, FontWeight = FontWeight.Medium },
    };

    /// <summary>A row: the disc saying what it is, then its lines.</summary>
    private static Grid RowLayout(string iconKey, Control lines)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*") };
        grid.Children.Add(LibraryLook.RowDisc(iconKey));
        lines.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        return grid;
    }

    /// <summary>The time, and a small star when kept: the second line of a row.</summary>
    private static StackPanel TimeLine(TextBlock time, bool kept)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { time } };
        if (kept)
        {
            LucideIcon star = LibraryLook.Icon("IconStar", 9);
            star.Foreground = StatusBrushes.Attention;
            ToolTip.SetTip(star, "Kept");
            line.Children.Add(star);
        }

        return line;
    }

    private ListBoxItem DictationRow(LibraryItem.Dictation item)
    {
        HistoryEntry entry = item.Entry;
        bool hasWords = entry.Text.Length > 0;
        var words = new TextBlock
        {
            Classes = { "words14" },
            Text = hasWords ? FirstWords(entry.Text) : "Nothing recognized",
            FontStyle = hasWords ? FontStyle.Normal : FontStyle.Italic,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Only the wordless row is grey.
        if (!hasWords) { ThemeTokens.Bind(words, TextBlock.ForegroundProperty, ThemeTokens.Label2); }
        var time = new TextBlock
        {
            Classes = { "small", "tnum" },
            Text = entry.Date.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        };
        Grid grid = RowLayout("IconQuote", new StackPanel { Spacing = 2, Children = { words, TimeLine(time, entry.IsKept) } });

        var copy = new MenuItem { Header = "Copy", IsEnabled = hasWords };
        copy.Click += (_, _) => state.CopyHistoryEntry(entry);
        var keep = new MenuItem { Header = entry.IsKept ? "Stop keeping this one" : "Keep this one" };
        keep.Click += (_, _) => state.SetHistoryEntryKept(!entry.IsKept, entry);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _ = DeleteItems([item]);

        var row = new ListBoxItem
        {
            Classes = { "libraryRow" },
            Content = grid,
            Tag = item,
            ContextMenu = new ContextMenu { ItemsSource = new Control[] { copy, keep, new Separator(), delete } },
        };
        AutomationProperties.SetName(row, hasWords ? entry.Text : "Nothing recognized");
        return row;
    }

    private ListBoxItem RecordingRow(LibraryItem.Recording item, bool showsSeconds)
    {
        ILibraryRecording recording = item.Value;
        string kind = RecordingsPlaceholder.Kind(recording.Kind);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(new TextBlock
        {
            Classes = { "words14" },
            Text = recording.Title ?? kind,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var duration = new TextBlock { Classes = { "detail", "tnum" }, Text = Clock(recording.Duration), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(duration, 1);
        top.Children.Add(duration);

        string start = TimeZoneInfo.ConvertTime(recording.StartedAt, TimeZoneInfo.Local)
            .ToString(showsSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        var status = new TextBlock { Classes = { "small", "tnum" }, Text = start, Tag = start, TextTrimming = TextTrimming.CharacterEllipsis };
        statusLines[recording.Id] = status;

        var stack = new StackPanel { Spacing = 2, Children = { top, status } };
        if (recording is LibraryRecording { Metadata: { } metadata }
            && (RecordingsPlaceholder.EndNote(metadata.EndReason) ?? RecordingsPlaceholder.DegradedNote(metadata)) is { } warning)
        {
            LucideIcon alert = LibraryLook.Icon("IconTriangleAlert", 11);
            alert.Foreground = StatusBrushes.Attention;
            alert.VerticalAlignment = VerticalAlignment.Top;
            alert.Margin = new Thickness(0, 1, 5, 0);
            var text = new TextBlock
            {
                Text = warning,
                FontSize = 11,
                Foreground = StatusBrushes.Attention,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            };
            DockPanel.SetDock(alert, Dock.Left);
            stack.Children.Add(new DockPanel { Children = { alert, text } });
        }

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => Rename(recording.Id);
        var trash = new MenuItem { Header = "Move to Trash" };
        trash.Click += (_, _) => _ = DeleteItems([item]);
        var menu = new List<Control> { rename };
        if (recording is LibraryRecording { Metadata: { } exported })
        {
            var exportTranscript = new MenuItem { Header = "Export Transcript…" };
            exportTranscript.Click += (_, _) => _ = RecordingExport.Transcript(this, state, exported);
            var exportAudio = new MenuItem { Header = "Export Audio…" };
            exportAudio.Click += (_, _) => _ = RecordingExport.Audio(this, state, exported);
            menu.AddRange([new Separator(), exportTranscript, exportAudio]);
        }

        menu.AddRange([new Separator(), trash]);

        string icon = recording.Kind is RecordingKind.ScreenRecording ? "IconMonitor" : "IconMic";
        var row = new ListBoxItem
        {
            Classes = { "libraryRow" },
            Content = RowLayout(icon, stack),
            Tag = item,
            ContextMenu = new ContextMenu { ItemsSource = menu },
        };
        ToolTip.SetTip(row, TimeZoneInfo.ConvertTime(recording.StartedAt, TimeZoneInfo.Local).ToString("F", CultureInfo.CurrentCulture));
        AutomationProperties.SetName(row, recording.Title ?? RecordingsPlaceholder.DefaultTitle(recording.StartedAt));
        AutomationProperties.SetHelpText(row, kind);
        return row;
    }

    /// <summary>The recording in progress: a red dot, Recording or Paused, and how long so far.</summary>
    private ListBoxItem LiveRow(LibraryItem item)
    {
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 14, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var time = new TextBlock { Classes = { "clock" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        label.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(dot);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        var row = new ListBoxItem { Classes = { "libraryRow" }, Content = grid, Tag = item, Padding = new Thickness(10, 10) };
        liveRow = (row, dot, label, time);
        RenderLiveRow();
        return row;
    }

    private void RenderLiveRow()
    {
        if (liveRow is not { } live) { return; }
        bool paused = state.RecordingState == MeetingRecordingState.Paused;
        live.Dot.Fill = paused ? ThemeTokens.BrushOf(this, ThemeTokens.Label3) : StatusBrushes.Recording;
        live.State.Text = paused ? "Paused" : "Recording";
        live.Time.Text = RecordingTime.Clock(state.LiveDuration);
        AutomationProperties.SetName(live.Row, paused ? "Recording, paused" : "Recording");
        AutomationProperties.SetHelpText(live.Row, RecordingTime.Spoken(state.LiveDuration));
    }

    /// <summary>Import progress rewrites only the status lines; the start time comes back when it is done.</summary>
    private void RenderStatusLines()
    {
        RecordingImportActivity activity = state.ImportActivity;
        bool isDictating = state.DictationStateValue != DictationState.Idle;
        foreach ((Guid id, TextBlock line) in statusLines)
        {
            line.Text = RecordingsPlaceholder.ImportLine(id, activity, isDictating) ?? (string?)line.Tag;
        }
    }

    private static string FirstWords(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 120 ? flat : flat[..120];
    }

    private static string Clock(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // MARK: - Selection

    private LibraryItem? Primary => selection.Count == 1 ? listed.FirstOrDefault(item => item.Id == selection.First()) : null;

    private List<LibraryItem> SelectedItems => [.. listed.Where(item => selection.Contains(item.Id))];

    private void Select(IEnumerable<Guid> ids)
    {
        selection = [.. ids];
        updating = true;
        try
        {
            ApplySelectionToList();
        }
        finally
        {
            updating = false;
        }

        RenderDetail();
        RenderToolbar();
    }

    private void ApplySelectionToList(bool scroll = true)
    {
        ItemList.SelectedItems?.Clear();
        if (ItemList.ItemsSource is not IEnumerable<ListBoxItem> rows) { return; }
        ListBoxItem? last = null;
        foreach (ListBoxItem row in rows)
        {
            if (row.Tag is LibraryItem item && selection.Contains(item.Id))
            {
                ItemList.SelectedItems?.Add(row);
                last = row;
            }
        }

        if (scroll && last is not null) { ItemList.ScrollIntoView(last); }
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating) { return; }
        selection = [.. (ItemList.SelectedItems?.OfType<ListBoxItem>() ?? []).Select(row => row.Tag).OfType<LibraryItem>().Select(item => item.Id)];
        RenderDetail();
        RenderToolbar();
    }

    private void RenderDetail()
    {
        LibraryItem? primary = Primary;
        (Guid, bool)? key = primary is null ? null : (primary.Id, primary is LibraryItem.Recording);
        // The placeholder depends on the list too (nothing found / nothing selected): always redone.
        if (key is not null && key == detailFor && Detail.Content is not null) { return; }
        detailFor = key;
        DetachDetail();
        Detail.Content = CreateDetail(primary);
    }

    // MARK: - Record bar

    private bool IsLive(Guid id) => state.LiveRecordingId == id;

    private void RenderRecordBar()
    {
        RecordBar.IsVisible = state.CanRecord;
        MeetingRecordingState recording = state.RecordingState;
        bool live = recording is MeetingRecordingState.Recording or MeetingRecordingState.Paused;
        bool paused = recording == MeetingRecordingState.Paused;
        string clock = RecordingTime.Clock(state.LiveDuration);

        LiveStatusPanel.IsVisible = recording != MeetingRecordingState.Idle;
        // The red disc only while the microphone is live; a ring when paused, starting or saving.
        LiveDot.Kind = recording == MeetingRecordingState.Recording ? StateGlyphKind.Listening : StateGlyphKind.Silent;
        LiveStatus.Text = recording switch
        {
            MeetingRecordingState.Starting => "Starting…",
            MeetingRecordingState.Recording => "Recording",
            MeetingRecordingState.Paused => "Paused",
            MeetingRecordingState.Stopping => "Saving…",
            _ => string.Empty,
        };
        LiveClock.Text = clock;
        LiveClock.IsVisible = live;
        if (!live)
        {
            youLevels.Clear();
            othersLevels.Clear();
        }

        LiveMeters.IsVisible = live;
        OthersMeterPanel.IsVisible = state.LiveRecording?.IsMeeting == true;

        PauseButton.IsVisible = live;
        PauseIcon.Data = LibraryLook.Geometry(paused ? "IconPlay" : "IconPause");
        string pauseTip = paused ? "Resume recording" : "Pause recording";
        ToolTip.SetTip(PauseButton, pauseTip);
        AutomationProperties.SetName(PauseButton, pauseTip);

        // Record, then Stop in the same place; nothing to press while it starts or saves.
        RecordButton.IsVisible = recording is MeetingRecordingState.Idle || live;
        RecordDot.IsVisible = !live;
        StopIcon.IsVisible = live;
        RecordDot.Fill = StatusBrushes.Recording;
        RecordLabel.Text = live ? "Stop" : "Record";
        bool microphoneOnly = !state.SupportsSystemAudio || MicOnlyToggle.IsChecked == true;
        ToolTip.SetTip(RecordButton, live
            ? "Ends the recording and keeps it"
            : microphoneOnly ? "Records your voice" : "Records you and the other side");
        AutomationProperties.SetName(RecordButton, live ? "Stop recording" : "Record");
        MicOnlyToggle.IsVisible = recording == MeetingRecordingState.Idle && state.SupportsSystemAudio;

        RenderLiveRow();
        RenderMeters();
    }

    /// <summary>Ten times a second: each side's newest peak joins its meter, the oldest leaves.</summary>
    private void PushLevels()
    {
        MeetingCapture.Levels levels = state.LiveLevels;
        Push(youLevels, levels.Microphone);
        Push(othersLevels, levels.System);
    }

    private static void Push(Queue<float> levels, float peak)
    {
        levels.Enqueue(Math.Clamp(peak, 0f, 1f));
        while (levels.Count > MeterBars) { levels.Dequeue(); }
    }

    /// <summary>
    /// One meter per side: accent while it arrives, the warning colour when it is not arriving,
    /// grey while paused.
    /// </summary>
    private void RenderMeters()
    {
        bool paused = state.RecordingState == MeetingRecordingState.Paused;
        YouMeter.Samples = [.. youLevels];
        OthersMeter.Samples = [.. othersLevels];
        Colour(YouMeter, state.LiveMicrophoneHealth.MarksRecordingDegraded, paused);
        Colour(OthersMeter, state.LiveCaptureHealth.MarksRecordingDegraded, paused);
    }

    private void Colour(LevelWaveform meter, bool degraded, bool paused) =>
        meter.Foreground = degraded ? StatusBrushes.Attention
            : paused ? null
            : ThemeTokens.BrushOf(this, ThemeTokens.Accent);

    private void OnRecord(object? sender, RoutedEventArgs args)
    {
        if (state.IsRecording)
        {
            _ = state.StopRecording();
            return;
        }

        // A voice note is never remembered: the next Record is a meeting again unless ticked.
        _ = state.StartRecording(includeSystemAudio: MicOnlyToggle.IsChecked != true);
    }

    private void OnPauseResume(object? sender, RoutedEventArgs args) =>
        _ = state.RecordingState == MeetingRecordingState.Paused ? state.ResumeRecording() : state.PauseRecording();

    // MARK: - Toolbar

    private void RenderToolbar()
    {
        RecordingImportActivity activity = state.ImportActivity;
        ImportButton.IsEnabled = state.CanImport;
        ImportSpinner.IsVisible = activity.Copying > 0;
        ImportLabel.IsVisible = activity.Copying == 0;

        LibraryItem? primary = Primary;
        // The recording in progress has no folder in the Library yet, and cannot be trashed.
        RevealButton.IsEnabled = primary is not null && !IsLive(primary.Id);
        ToolTip.SetTip(RevealButton, primary is LibraryItem.Dictation ? "Show in folder" : "Open recording's folder");

        CopyButton.IsEnabled = primary switch
        {
            LibraryItem.Dictation dictation => dictation.Entry.Text.Length > 0,
            LibraryItem.Recording recording => state.Transcript(recording.Id).Count > 0,
            _ => false,
        };
        string copyTip = primary is LibraryItem.Dictation ? "Copy this dictation" : "Copy all text transcribed so far";
        ToolTip.SetTip(CopyButton, copyTip);
        AutomationProperties.SetName(CopyButton, copyTip);

        TrashButton.IsVisible = selection.Count > 0;
        TrashButton.IsEnabled = !trashing && !(state.LiveRecordingId is { } live && selection.Contains(live));
        string trashTip = DeleteTitle();
        ToolTip.SetTip(TrashButton, trashTip);
        AutomationProperties.SetName(TrashButton, trashTip);
    }

    private string DeleteTitle()
    {
        List<LibraryItem> items = SelectedItems;
        int dictations = items.Count(item => item is LibraryItem.Dictation);
        if (dictations == items.Count) { return items.Count == 1 ? "Delete this dictation" : "Delete selected dictations"; }
        if (dictations == 0) { return items.Count == 1 ? "Move recording to Trash" : "Move selected recordings to Trash"; }
        return "Delete selected items";
    }

    private void OnImport(object? sender, RoutedEventArgs args) => _ = ChooseFiles();

    private async Task ChooseFiles()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Audio or Video",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio and video") { Patterns = ImportPatterns }],
        }).ConfigureAwait(true);
        Import(files.Select(file => file.TryGetLocalPath()));
    }

    private void Import(IEnumerable<string?> paths)
    {
        string[] local = [.. paths.OfType<string>()];
        if (local.Length > 0) { _ = state.ImportFiles(local); }
    }

    private void OnReveal(object? sender, RoutedEventArgs args)
    {
        switch (Primary)
        {
            case LibraryItem.Dictation:
                state.RevealHistoryAudio();
                break;
            case LibraryItem.Recording recording:
                state.RevealRecording(recording.Id);
                break;
        }
    }

    private void OnCopy(object? sender, RoutedEventArgs args)
    {
        switch (Primary)
        {
            case LibraryItem.Dictation dictation:
                state.CopyHistoryEntry(dictation.Entry);
                break;
            case LibraryItem.Recording recording:
                state.CopyTranscript(recording.Id);
                break;
        }
    }

    private void OnTrash(object? sender, RoutedEventArgs args) => _ = DeleteItems(SelectedItems);

    // MARK: - Deleting and renaming

    /// <summary>
    /// Dictations are deleted for good, audio and all; recordings go to the Trash - and where this
    /// desktop has none, only after the person agreed to lose them.
    /// </summary>
    private async Task DeleteItems(IReadOnlyCollection<LibraryItem> items)
    {
        Guid[] dictations = [.. items.OfType<LibraryItem.Dictation>().Select(item => item.Id)];
        Guid[] recordings = [.. items.OfType<LibraryItem.Recording>().Select(item => item.Id).Where(id => !IsLive(id))];
        if (dictations.Length > 0) { state.DeleteDictations(dictations); }
        if (recordings.Length == 0 || trashing) { return; }
        RecordingTrashOutcome outcome;
        SetTrashing(true);
        try
        {
            // gio runs once per recording, with a timeout of its own: off the UI thread.
            outcome = await state.TrashRecordingsAsync(recordings).ConfigureAwait(true);
        }
        finally
        {
            SetTrashing(false);
        }

        if (outcome != RecordingTrashOutcome.NeedsPermanentDeletion) { return; }

        bool confirmed = await Confirm.Ask(
            this,
            recordings.Length == 1 ? "Delete this recording?" : "Delete these recordings?",
            "This desktop has no Trash to move it to, so it is deleted from this PC for good, with its audio and transcript.",
            "Delete").ConfigureAwait(true);
        if (confirmed) { state.DeleteRecordingsPermanently(recordings); }
    }

    private void SetTrashing(bool busy)
    {
        trashing = busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
        RenderToolbar();
    }

    /// <summary>Renaming happens in the detail pane's title: the recording is selected, then its title opens.</summary>
    private void Rename(Guid id)
    {
        if (IsLive(id)) { return; }
        Select([id]);
        // A pane made just now is not in the window yet; its title opens once it is (and takes the
        // caret after that, once the menu or key that asked has let go of the focus).
        Dispatcher.UIThread.Post(
            () => (Detail.Content as RecordingDetailView)?.BeginRename(),
            DispatcherPriority.Loaded);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Delete when selection.Count > 0:
                _ = DeleteItems(SelectedItems);
                args.Handled = true;
                break;
            case Key.Enter when Primary is LibraryItem.Recording recording:
                Rename(recording.Id);
                args.Handled = true;
                break;
        }
    }

    // MARK: - Drag and drop

    private void OnDragEnter(object? sender, DragEventArgs args)
    {
        bool files = args.DataTransfer.Contains(DataFormat.File);
        args.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = files && state.CanImport;
    }

    /// <summary>
    /// Leave and enter bubble up from every child the pointer crosses; the overlay follows the
    /// last over, so crossing from one child to the next does not leave it hidden.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs args)
    {
        bool accepts = args.DataTransfer.Contains(DataFormat.File) && state.CanImport;
        args.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = accepts;
    }

    private void OnDrop(object? sender, DragEventArgs args)
    {
        DropOverlay.IsVisible = false;
        if (!state.CanImport) { return; }
        Import((args.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()));
    }
}
