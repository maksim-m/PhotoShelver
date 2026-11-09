using PhotoShelver.Core.Planning;

namespace PhotoShelver.App.ViewModels;

/// <summary>
/// Target tree node. Expansion default: expanded when files will be added inside, collapsed otherwise.
/// Manual expand/collapse by the user is reported back so it survives tree rebuilds.
/// </summary>
public sealed class FolderNodeViewModel
{
    private readonly Action<string, bool> _onUserToggle;
    private bool _isExpanded;

    public FolderNodeViewModel(FolderNode node, FolderNodeViewModel? parent, IReadOnlyDictionary<string, bool> expansionOverrides, Action<string, bool> onUserToggle)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        IsNew = node.IsNew;
        Count = node.Count;
        Parent = parent;
        _onUserToggle = onUserToggle;
        _isExpanded = expansionOverrides.TryGetValue(FullPath, out var manual) ? manual : node.Count > 0;
        Children = node.Children.Select(c => new FolderNodeViewModel(c, this, expansionOverrides, onUserToggle)).ToList();
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsNew { get; }
    public int Count { get; }
    public FolderNodeViewModel? Parent { get; }
    public bool HasCount => Count > 0;
    public string CountText => $"+{Count}";

    /// <summary>Accessible name, e.g. "2026-11-05 +2 NEW" (screen readers and UI tests).</summary>
    public string AutomationName => Name + (HasCount ? " " + CountText : "") + (IsNew ? " NEW" : "");
    public IReadOnlyList<FolderNodeViewModel> Children { get; }

    /// <summary>Bound two-way to the TreeViewItem; a change coming from the UI is a user action.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            _onUserToggle(FullPath, value);
        }
    }

    /// <summary>Sets the default state without recording it as a user choice (call before binding).</summary>
    internal void SetDefaultExpanded(bool value) => _isExpanded = value;

    /// <summary>Path from the root to this node.</summary>
    public IReadOnlyList<FolderNodeViewModel> PathFromRoot()
    {
        var path = new List<FolderNodeViewModel>();
        for (var n = this; n is not null; n = n.Parent) path.Add(n);
        path.Reverse();
        return path;
    }
}
