namespace PhotoImporter.App.ViewModels;

/// <summary>
/// The thumbnail grid is a virtualized list of rows: date headers and rows of tiles.
/// (WPF has no virtualizing wrap panel, so the wrapping is done here.)
/// </summary>
public abstract class GridRow;

public sealed class HeaderRow(string title, string subtitle) : GridRow
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}

public sealed class TileRow(IReadOnlyList<MediaItemViewModel> items) : GridRow
{
    public IReadOnlyList<MediaItemViewModel> Items { get; } = items;
}
