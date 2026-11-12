using System.Collections.ObjectModel;
using System.ComponentModel;
using PhotoShelver.Core.Planning;

namespace PhotoShelver.App.ViewModels;

/// <summary>
/// Target tree node. Expansion default: expanded when files will be added inside, collapsed otherwise.
/// Manual expand/collapse by the user is reported back so it survives tree updates.
/// The tree is updated in place (<see cref="Sync"/>): ticking files only changes counts and labels, so the TreeView keeps
/// its items instead of creating them all again (hundreds of folders when a year is open).
/// </summary>
public sealed class FolderNodeViewModel : INotifyPropertyChanged
{
    private readonly Action<string, bool> _onUserToggle;
    private bool _isExpanded;
    private int _count;
    private bool _isNew;

    private FolderNodeViewModel(FolderNode node, FolderNodeViewModel? parent, IReadOnlyDictionary<string, bool> expansionOverrides, Action<string, bool> onUserToggle)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        Parent = parent;
        _onUserToggle = onUserToggle;
        _count = node.Count;
        _isNew = node.IsNew;
        _isExpanded = expansionOverrides.TryGetValue(FullPath, out var manual) ? manual : node.Count > 0;
        Sync(Children, node.Children, this, expansionOverrides, onUserToggle);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string FullPath { get; }
    public FolderNodeViewModel? Parent { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    public int Count => _count;
    public bool IsNew => _isNew;
    public bool HasCount => Count > 0;
    public string CountText => $"+{Count}";

    /// <summary>Accessible name, e.g. "2026-11-05 +2 NEW" (screen readers and UI tests).</summary>
    public string AutomationName => Name + (HasCount ? " " + CountText : "") + (IsNew ? " NEW" : "");

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

    /// <summary>Sets the default state without recording it as a user choice.</summary>
    internal void SetDefaultExpanded(bool value)
    {
        if (_isExpanded == value) return;
        _isExpanded = value;
        Raise(nameof(IsExpanded));
    }

    /// <summary>Path from the root to this node.</summary>
    public IReadOnlyList<FolderNodeViewModel> PathFromRoot()
    {
        var path = new List<FolderNodeViewModel>();
        for (var n = this; n is not null; n = n.Parent) path.Add(n);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Makes <paramref name="target"/> show <paramref name="nodes"/> (same order): nodes with the same path keep their
    /// view model and only get the new count, label and default expansion; others are added or removed.
    /// </summary>
    public static void Sync(ObservableCollection<FolderNodeViewModel> target, IReadOnlyList<FolderNode> nodes, FolderNodeViewModel? parent,
        IReadOnlyDictionary<string, bool> expansionOverrides, Action<string, bool> onUserToggle)
    {
        var existing = target.ToDictionary(vm => vm.FullPath, StringComparer.OrdinalIgnoreCase);
        var wanted = new List<FolderNodeViewModel>(nodes.Count);
        foreach (var node in nodes)
        {
            if (existing.TryGetValue(node.FullPath, out var vm))
            {
                vm.Update(node, expansionOverrides);
                Sync(vm.Children, node.Children, vm, expansionOverrides, onUserToggle);
            }
            else
            {
                vm = new FolderNodeViewModel(node, parent, expansionOverrides, onUserToggle);
            }
            wanted.Add(vm);
        }

        // Remove what is gone, then put the rest in order with as few collection changes as possible.
        var keep = new HashSet<FolderNodeViewModel>(wanted);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            var at = target.IndexOf(wanted[i]);
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, wanted[i]);
        }
    }

    private void Update(FolderNode node, IReadOnlyDictionary<string, bool> expansionOverrides)
    {
        var changed = _count != node.Count || _isNew != node.IsNew;
        _count = node.Count;
        _isNew = node.IsNew;
        if (changed)
        {
            Raise(nameof(Count));
            Raise(nameof(IsNew));
            Raise(nameof(HasCount));
            Raise(nameof(CountText));
            Raise(nameof(AutomationName));
        }
        // Like a fresh tree: open where files go, unless the user opened or closed this folder.
        SetDefaultExpanded(expansionOverrides.TryGetValue(FullPath, out var manual) ? manual : node.Count > 0);
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
