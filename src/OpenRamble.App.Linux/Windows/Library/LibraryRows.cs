using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// One row of the Library's list: a day header, a dictation or recording, or the recording in
/// progress. Rows live as long as the thing they show is listed, so the list keeps its
/// containers - and where it is scrolled to - across every change beside it.
/// </summary>
internal abstract class LibraryRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Who this row is across rebuilds: the same key is the same row, updated in place.</summary>
    public abstract string Key { get; }

    /// <summary>The selectable thing behind the row; none for a day header.</summary>
    public virtual LibraryItem? Item => null;

    /// <summary>Takes what a fresh row of the same key holds; says whether anything changed.</summary>
    public abstract bool UpdateFrom(LibraryRow fresh);

    /// <summary>The row's look has to be made again.</summary>
    protected void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed class DayHeaderRow(DateOnly day, string title) : LibraryRow
{
    public string Title { get; private set; } = title;

    public override string Key => $"day {day:yyyy-MM-dd}";

    public override bool UpdateFrom(LibraryRow fresh)
    {
        string title = ((DayHeaderRow)fresh).Title;
        if (title == Title) { return false; }
        Title = title;
        Changed();
        return true;
    }
}

/// <summary>A dictation or a recording; a recording's status line is rewritten as an import advances.</summary>
internal sealed class ItemRow(LibraryItem item, bool showsSeconds) : LibraryRow
{
    private LibraryItem item = item;
    private string? status;

    public override LibraryItem Item => item;

    public bool ShowsSeconds { get; private set; } = showsSeconds;

    /// <summary>What the status line says instead of the start time; <c>null</c> - the time.</summary>
    public string? Status
    {
        get => status;
        set
        {
            if (status == value) { return; }
            status = value;
            Changed();
        }
    }

    public override string Key => $"item {item.Id}";

    public override bool UpdateFrom(LibraryRow fresh)
    {
        var other = (ItemRow)fresh;
        if (Equals(item, other.item) && ShowsSeconds == other.ShowsSeconds) { return false; }
        item = other.item;
        ShowsSeconds = other.ShowsSeconds;
        Changed();
        return true;
    }
}

/// <summary>The recording in progress, pinned above everything; its state and time tick in place.</summary>
internal sealed class LiveRow(LibraryItem item) : LibraryRow
{
    private LibraryItem item = item;

    public override LibraryItem Item => item;

    public bool Paused { get; private set; }

    public TimeSpan Duration { get; private set; }

    public override string Key => $"live {item.Id}";

    public override bool UpdateFrom(LibraryRow fresh)
    {
        item = fresh.Item!;
        return false;
    }

    public void Tick(bool paused, TimeSpan duration)
    {
        if (paused == Paused && duration == Duration) { return; }
        Paused = paused;
        Duration = duration;
        Changed();
    }
}

/// <summary>A row's look, made by the window and made again whenever the row changes.</summary>
internal sealed class LibraryRowView : Decorator
{
    private readonly Func<LibraryRow, Control> build;
    private LibraryRow? row;

    public LibraryRowView(Func<LibraryRow, Control> build) => this.build = build;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (row is not null) { row.PropertyChanged -= OnRowChanged; }
        row = DataContext as LibraryRow;
        if (row is not null) { row.PropertyChanged += OnRowChanged; }
        Child = row is null ? null : build(row);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs args) => Child = row is null ? null : build(row);

    /// <summary>The list's one template: every row kind through the window's builder.</summary>
    public static IDataTemplate Template(Func<LibraryRow, Control> build) =>
        new FuncDataTemplate<LibraryRow>((_, _) => new LibraryRowView(build), supportsRecycling: true);
}

internal static class LibraryRowsSync
{
    /// <summary>
    /// Make <paramref name="rows"/> what <paramref name="fresh"/> lists, by key: rows no longer
    /// listed are removed, new ones inserted, the rest moved into place and updated - never
    /// replaced, so their containers stay.
    /// </summary>
    /// <returns>The rows whose content changed in place.</returns>
    public static List<LibraryRow> Sync(ObservableCollection<LibraryRow> rows, IReadOnlyList<LibraryRow> fresh)
    {
        var wanted = fresh.Select(row => row.Key).ToHashSet();
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(rows[index].Key)) { rows.RemoveAt(index); }
        }

        var updated = new List<LibraryRow>();
        for (int index = 0; index < fresh.Count; index++)
        {
            LibraryRow next = fresh[index];
            int at = -1;
            for (int search = index; search < rows.Count; search++)
            {
                if (rows[search].Key == next.Key)
                {
                    at = search;
                    break;
                }
            }

            if (at < 0)
            {
                rows.Insert(index, next);
                continue;
            }

            if (at != index) { rows.Move(at, index); }
            if (rows[index].UpdateFrom(next)) { updated.Add(rows[index]); }
        }

        return updated;
    }
}
