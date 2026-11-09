using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoImporter.App.Services;
using PhotoImporter.Core;
using PhotoImporter.Core.Scanning;

namespace PhotoImporter.App.ViewModels;

public sealed partial class MediaItemViewModel : ObservableObject
{
    private readonly ThumbnailService _thumbnails;
    private readonly Action _onSelectionChanged;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    public MediaItemViewModel(SourceFile file, ThumbnailService thumbnails, Action onSelectionChanged)
    {
        File = file;
        _thumbnails = thumbnails;
        _onSelectionChanged = onSelectionChanged;
    }

    public SourceFile File { get; }

    /// <summary>False once the item was replaced by a rescan (pending thumbnail loads are dropped).</summary>
    public bool IsAlive { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed), nameof(ToolTip), nameof(StatusGlyph), nameof(StatusTip))]
    private DuplicateInfo? _duplicate;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    public string FileName => File.FileName;

    public string Extension => Path.GetExtension(File.Path).TrimStart('.').ToUpperInvariant();

    public bool IsVideo => File.Kind == MediaKind.Video;

    public bool HasError => File.Error is not null;

    public DuplicateState State => Duplicate?.State ?? DuplicateState.New;

    public bool IsNew => !HasError && State == DuplicateState.New;

    public bool IsDimmed => !IsNew;

    public bool CanBeImported => !HasError && File.Date is not null;

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _thumbnails.Request(this);
            }
            return _thumbnail;
        }
    }

    [ObservableProperty]
    private bool _thumbnailMissing;

    internal void SetThumbnail(ImageSource? image)
    {
        _thumbnail = image;
        ThumbnailMissing = image is null;
        OnPropertyChanged(nameof(Thumbnail));
    }

    internal void EvictThumbnail()
    {
        _thumbnail = null;
        _thumbnailRequested = false;
    }

    /// <summary>Small badge glyph (Segoe Fluent Icons) in the corner of the tile, or null.</summary>
    public string? StatusGlyph =>
        HasError ? "" // Error
        : File.Date?.Source switch
        {
            DateSource.FileModified => "",        // Clock: date from file modification time
            DateSource.MetadataGuessedZone => "", // Globe: time zone guessed
            _ => null,
        };

    public string? StatusTip =>
        HasError ? L.T($"Read error: {File.Error}", $"Ошибка чтения: {File.Error}")
        : File.Date?.Source switch
        {
            DateSource.FileModified => L.T("No capture date in the metadata — the file modification date is used", "В метаданных нет даты съёмки — использована дата изменения файла"),
            DateSource.MetadataGuessedZone => L.T("The capture time zone is unknown — this computer's time zone is used", "Часовой пояс съёмки неизвестен — использован пояс этого компьютера"),
            _ => null,
        };

    public string ToolTip
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(File.Path);
            sb.Append(Format.Bytes(File.Size));
            if (File.Date is { } d) sb.Append(" · ").Append(Format.DateTime(d.Local));
            if (StatusTip is { } tip) sb.AppendLine().Append(tip);
            switch (Duplicate)
            {
                case { State: DuplicateState.InTarget, OtherPath: var p }:
                    sb.AppendLine().Append(L.T("Already imported: ", "Уже импортирован: ")).Append(p);
                    break;
                case { State: DuplicateState.DuplicateInSource, OtherPath: var p }:
                    sb.AppendLine().Append(L.T("Duplicate of a source file: ", "Дубликат файла в источнике: ")).Append(p);
                    break;
            }
            return sb.ToString();
        }
    }

    /// <summary>Called after every duplicate check: selection is reset to the default "all new files".</summary>
    internal void ApplyDuplicate(DuplicateInfo? info)
    {
        Duplicate = info;
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanBeImported));
        IsSelected = IsNew;
    }
}
