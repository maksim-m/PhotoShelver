using PhotoShelver.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoShelver.App.ViewModels;

/// <summary>
/// The thumbnail grid is a virtualized list of rows: day headers and rows of tiles.
/// (WPF has no virtualizing wrap panel, so the wrapping is done here.)
/// </summary>
public abstract class GridRow : ObservableObject;

/// <summary>Collapsible day header.</summary>
public sealed partial class HeaderRow : GridRow
{
    public HeaderRow(DateTime? day, string title, IReadOnlyList<MediaItemViewModel> items, bool isCollapsed)
    {
        Day = day;
        Title = title;
        Items = items;
        _isCollapsed = isCollapsed;
    }

    /// <summary>Group key: the capture day, or null for files that could not be read.</summary>
    public DateTime? Day { get; }

    public string Title { get; }

    public IReadOnlyList<MediaItemViewModel> Items { get; }

    [ObservableProperty] private bool _isCollapsed;

    public string Subtitle
    {
        get
        {
            var selected = Items.Count(i => i.IsSelected && i.CanBeImported);
            return $"{Format.Files(Items.Count)} · {Strings.Selected} {selected}";
        }
    }

    /// <summary>
    /// Day checkbox: true = all new files of the day are selected, false = nothing selected, null = partly.
    /// Setting it (a click) toggles: select all new files, or clear the whole day when already fully selected
    /// (or when the day has no new files). Duplicates are never selected in bulk.
    /// </summary>
    public bool? GroupSelection
    {
        get
        {
            var anySelected = Items.Any(i => i.IsSelected);
            var newItems = Items.Where(i => i.IsNew).ToList();
            if (!anySelected) return false;
            return newItems.Count > 0 && newItems.All(i => i.IsSelected) ? true : null;
        }
        set
        {
            var current = GroupSelection;
            var hasNew = Items.Any(i => i.IsNew);
            if (current == true || !hasNew)
            {
                foreach (var item in Items) item.IsSelected = false;
            }
            else
            {
                foreach (var item in Items.Where(i => i.IsNew)) item.IsSelected = true;
            }
            RefreshCounts();
        }
    }

    /// <summary>The day checkbox does something: there are new files to select or a selection to clear.</summary>
    public bool CanToggleSelection => Items.Any(i => i.IsNew || i.IsSelected);

    public string SelectionTip => Items.Any(i => i.IsNew)
        ? Strings.SelectAllNewFilesOfThisDay
        : Strings.NoNewFilesOnThisDay;

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(GroupSelection));
        OnPropertyChanged(nameof(CanToggleSelection));
        OnPropertyChanged(nameof(SelectionTip));
    }
}

public sealed class TileRow(IReadOnlyList<MediaItemViewModel> items) : GridRow
{
    public IReadOnlyList<MediaItemViewModel> Items { get; } = items;
}
