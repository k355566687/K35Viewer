using CommunityToolkit.Mvvm.ComponentModel;
using K35Viewer.Domain;

namespace K35Viewer.ViewModels;

/// <summary>
/// 左ナビゲーションのツリーの1項目。Id が null の項目は「すべて」(全登録フォルダを横断表示)。
/// </summary>
public sealed partial class FolderTreeItem : ObservableObject
{
    private FolderTreeItem(
        long? id,
        string header,
        IReadOnlyList<FolderTreeItem> children,
        long? libraryFolderId = null,
        string? fullPath = null)
    {
        Id = id;
        Header = header;
        Children = children;
        LibraryFolderId = libraryFolderId;
        FullPath = fullPath;
    }

    public long? Id { get; }

    /// <summary>管理フォルダ（登録フォルダ）自身の場合は、その登録の ID。サブフォルダと「すべて」は null。</summary>
    public long? LibraryFolderId { get; }

    /// <summary>フォルダのフルパス。「すべて」は null。</summary>
    public string? FullPath { get; }

    /// <summary>管理フォルダ（登録フォルダ）自身か。「管理対象から外す」ができるのは、これだけ。</summary>
    public bool IsLibraryRoot => LibraryFolderId is not null;

    public string Header { get; }

    public IReadOnlyList<FolderTreeItem> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public static FolderTreeItem CreateAll() => new(null, "すべて", []);

    public static FolderTreeItem From(FolderNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var header = node.IsUnavailable ? node.Name + "(接続なし)" : node.Name;
        return new FolderTreeItem(
            node.Id,
            header,
            node.Children.Select(From).ToList(),
            node.IsLibraryRoot ? node.LibraryFolderId : null,
            node.FullPath);
    }

    /// <summary>自身と配下から、指定したIDの項目を探す。</summary>
    public FolderTreeItem? Find(long id)
    {
        if (Id == id)
        {
            return this;
        }

        foreach (var child in Children)
        {
            if (child.Find(id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>自身と配下で、展開されている項目のIDを集める。</summary>
    public void CollectExpandedIds(ISet<long> ids)
    {
        if (IsExpanded && Id is { } id)
        {
            ids.Add(id);
        }

        foreach (var child in Children)
        {
            child.CollectExpandedIds(ids);
        }
    }

    public void ApplyExpandedIds(IReadOnlySet<long> ids)
    {
        if (Id is { } id && ids.Contains(id))
        {
            IsExpanded = true;
        }

        foreach (var child in Children)
        {
            child.ApplyExpandedIds(ids);
        }
    }
}
