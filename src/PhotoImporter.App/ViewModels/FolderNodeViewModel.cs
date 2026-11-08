using PhotoImporter.Core.Planning;

namespace PhotoImporter.App.ViewModels;

public sealed class FolderNodeViewModel
{
    public FolderNodeViewModel(FolderNode node)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        IsNew = node.IsNew;
        Count = node.Count;
        IsExpanded = node.Count > 0;
        Children = node.Children.Select(c => new FolderNodeViewModel(c)).ToList();
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsNew { get; }
    public int Count { get; }
    public bool HasCount => Count > 0;
    public string CountText => $"+{Count}";
    public bool IsExpanded { get; set; }
    public IReadOnlyList<FolderNodeViewModel> Children { get; }
}
