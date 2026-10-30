using System.Collections.ObjectModel;
using DynamicData;

namespace SilverModManager.App.UI.Helpers.TreeDataGrid;

/// <summary>
///     This interface provides the implementation for ViewModels which are based on
///     <see cref="DynamicData" />'s <see cref="ObservableCacheEx.TransformToTree{TObject,TKey}" /> method.
///     <br />
///     This interface provides the implementation for ViewModels to accept a
///     <see cref="Node{TItem,TKey}" /> and assign it to the current ViewModel.
/// </summary>
public interface IDynamicDataTreeItem<TItem, TKey>
    where TItem : class, IDynamicDataTreeItem<TItem, TKey>
    where TKey : notnull
{
    // Note: The setters below are intended only for internal use.
    // However they are marked 'protected internal' to allow external classes
    // to implement the interface(s).
    
    /// <summary>
    ///     Collection of child nodes.
    ///     This is an observable collection so that the UI can be
    ///     notified of changes to the tree structure.
    /// </summary>
    public ReadOnlyObservableCollection<TItem>? Children { get; protected internal set; }

    /// <summary>
    ///     The parent of the current node.
    /// </summary>
    TItem? Parent { get; protected internal set; }

    /// <summary>
    ///     The Id used in the <see cref="SourceCache{TObject,TKey}" /> for the original item.
    /// </summary>
    /// <remarks>
    ///     This field is used to uniquely identify this item.
    ///     **THIS FIELD MUST NEVER BE CHANGED** after setting an initial value.
    /// </remarks>
    TKey Key { get; protected internal set; }
}

public static class DynamicDataTreeItemExtensions
{
    /// <summary>
    ///     Initializes this <see cref="IDynamicDataTreeItem{TItem,TKey}" />.
    ///     <br />
    ///     Call this after DynamicData's <see cref="ObservableCacheEx.TransformToTree{TObject,TKey}" /> method.
    ///     Creates a new <see cref="TreeNodeVM{TItem,TKey}" /> from a <see cref="Node{TItem,TKey}" />.
    ///     <see cref="Node{TItem,TKey}" /> is the output of DynamicData TransformToTree().
    /// </summary>
    /// <param name="self">The ViewModel itself.</param>
    /// <param name="node">The DynamicData node type that wraps this element.</param>
    /// <param name="parent">The parent of the current node.</param>
    public static TItem Initialize<TSelf, TItem, TKey>(this TSelf self, Node<TItem, TKey> node, IDynamicDataTreeItem<TItem, TKey>? parent = null)
        where TSelf : IDynamicDataTreeItem<TItem, TKey> // <= hint for potential JIT devirtualization
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        self.Key = node.Key;
        self.Parent = parent as TItem;
        node.Children
            .Connect()
            .Transform(child => child.Item.Initialize(child, self))
            .Bind(out var children)
            .Subscribe();

        self.Children = children;
        return AsItem<TItem, TKey>(self);
    }

    /// <summary>
    ///     Returns a collection of all the descendent <typeparamref name="TKey" />
    ///     of this node (excluding this node).
    /// </summary>
    public static List<TKey> GetAllDescendentKeys<TSelf, TItem, TKey>(this TSelf self)
        where TSelf : IDynamicDataTreeItem<TItem, TKey> // <= hint for potential JIT devirtualization
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        var results = new List<TKey>();
        if (self.Children?.Count == 0)
            return results;

        GetAllDescendentKeysRecursive(AsItem<TItem, TKey>(self), results);
        return results;
    }

    private static void GetAllDescendentKeysRecursive<TItem, TKey>(TItem node, List<TKey> results)
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        foreach (var child in RequiredChildren<TItem, TKey>(node))
        {
            results.Add(child.Key);
            GetAllDescendentKeysRecursive(child, results);
        }
    }

    /// <summary>
    ///     Returns a collection of all the descendent nodes of this node (excluding this node).
    /// </summary>
    /// <returns></returns>
    public static List<TItem> GetAllDescendentNodes<TSelf, TItem, TKey>(this TSelf self)
        where TSelf : IDynamicDataTreeItem<TItem, TKey> // <= hint for potential JIT devirtualization
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        var results = new List<TItem>();
        if (self.Children?.Count == 0)
            return results;

        GetAllDescendentNodesRecursive<TItem, TKey>(AsItem<TItem, TKey>(self), results);
        return results;
    }

    private static void GetAllDescendentNodesRecursive<TItem, TKey>(TItem node, List<TItem> results)
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        foreach (var child in RequiredChildren<TItem, TKey>(node))
        {
            results.Add(child);
            GetAllDescendentNodesRecursive<TItem, TKey>(child, results);
        }
    }

    /// <summary>
    ///     The item an implementation passes as itself. A plain cast, so a type that names a different
    ///     <typeparamref name="TItem" /> fails with the two type names.
    /// </summary>
    private static TItem AsItem<TItem, TKey>(IDynamicDataTreeItem<TItem, TKey> self)
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        return (TItem)self;
    }

    private static ReadOnlyObservableCollection<TItem> RequiredChildren<TItem, TKey>(TItem node)
        where TItem : class, IDynamicDataTreeItem<TItem, TKey>
        where TKey : notnull
    {
        return node.Children
            ?? throw new InvalidOperationException($"{typeof(TItem).Name} '{node.Key}' has no Children; call Initialize on it first");
    }
}
