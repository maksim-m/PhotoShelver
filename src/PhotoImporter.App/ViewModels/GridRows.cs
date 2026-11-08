using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoImporter.App.ViewModels;

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
            return $"{Format.Files(Items.Count)} · выбрано {selected}";
        }
    }

    public void RefreshCounts() => OnPropertyChanged(nameof(Subtitle));
}

public sealed class TileRow(IReadOnlyList<MediaItemViewModel> items) : GridRow
{
    public IReadOnlyList<MediaItemViewModel> Items { get; } = items;
}
