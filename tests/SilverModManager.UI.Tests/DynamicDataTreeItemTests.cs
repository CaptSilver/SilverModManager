using System.Collections.ObjectModel;
using FluentAssertions;
using SilverModManager.App.UI.Helpers.TreeDataGrid;

namespace SilverModManager.UI.Tests;

public class DynamicDataTreeItemTests
{
    private sealed class Item : IDynamicDataTreeItem<Item, int>
    {
        public ReadOnlyObservableCollection<Item>? Children { get; set; }
        public Item? Parent { get; set; }
        public int Key { get; set; }
    }

    [Fact]
    public void Test_GetAllDescendentNodes_ThrowsNamingTheItemWhenTheTreeWasNeverInitialized()
    {
        var item = new Item { Key = 7 };

        var act = () => item.GetAllDescendentNodes<Item, Item, int>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Item*Children*");
    }

    [Fact]
    public void Test_GetAllDescendentKeys_ThrowsNamingTheItemWhenTheTreeWasNeverInitialized()
    {
        var item = new Item { Key = 7 };

        var act = () => item.GetAllDescendentKeys<Item, Item, int>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Item*Children*");
    }

    [Fact]
    public void Test_GetAllDescendentNodes_ListsTheChildrenOfAnInitializedTree()
    {
        var child = new Item { Key = 2, Children = new ReadOnlyObservableCollection<Item>(new ObservableCollection<Item>()) };
        var root = new Item { Key = 1, Children = new ReadOnlyObservableCollection<Item>(new ObservableCollection<Item> { child }) };

        root.GetAllDescendentNodes<Item, Item, int>().Should().Equal(child);
        root.GetAllDescendentKeys<Item, Item, int>().Should().Equal(2);
    }
}
