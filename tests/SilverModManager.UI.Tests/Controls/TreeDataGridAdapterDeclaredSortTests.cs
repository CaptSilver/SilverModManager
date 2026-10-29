using System.ComponentModel;
using Avalonia.Controls.Models.TreeDataGrid;
using DynamicData;
using FluentAssertions;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>
/// A column declared with a sort direction shows its arrow, but the grid only sorts once it is told to. Without that
/// the rows stay in the order they arrive from the database, which varies from load to load.
/// </summary>
public class TreeDataGridAdapterDeclaredSortTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class DatedAdapter(IServiceProvider provider, IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> roots)
        : TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>(provider)
    {
        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical) => roots;

        protected override IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical) =>
        [
            ITreeDataGridItemModel<CompositeItemModel<EntityId>, EntityId>.CreateExpanderColumn(ColumnCreator.Create<EntityId, SharedColumns.Name>()),
            ColumnCreator.Create<EntityId, SharedColumns.InstalledDate>(sortDirection: ListSortDirection.Descending),
        ];
    }

    private static CompositeItemModel<EntityId> Row(ulong id, string name, int daysAgo)
    {
        var model = new CompositeItemModel<EntityId>(EntityId.From(0x200000000000000 + id));
        model.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: name));
        model.Add(SharedColumns.InstalledDate.ComponentKey, new DateComponent(DateTimeOffset.UtcNow.AddDays(-daysAgo)));
        return model;
    }

    private static string[] NamesInGridOrder(DatedAdapter adapter) => Enumerable.Range(0, adapter.Source.Value.Rows.Count)
        .Select(i => ((CompositeItemModel<EntityId>)adapter.Source.Value.Rows[i].Model!).Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value)
        .ToArray();

    [Fact]
    public async Task RowsAreSortedByTheDeclaredColumnWhateverOrderTheyArriveIn()
    {
        var roots = new SourceCache<CompositeItemModel<EntityId>, EntityId>(static model => model.Key);
        var adapter = new DatedAdapter(Provider, roots.Connect());
        using var activation = adapter.Activate();

        // Oldest first, the order a fresh database tends to deliver them in.
        roots.AddOrUpdate([Row(1, "oldest", daysAgo: 3), Row(2, "middle", daysAgo: 2)]);
        await EventuallyOnUi(() => NamesInGridOrder(adapter).Should().HaveCount(2));
        NamesInGridOrder(adapter).Should().Equal("middle", "oldest");

        // Rows that arrive later find their place too.
        roots.AddOrUpdate(Row(3, "newest", daysAgo: 1));
        await EventuallyOnUi(() => NamesInGridOrder(adapter).Should().Equal("newest", "middle", "oldest"));
    }

    [Fact]
    public async Task AColumnTheUserSortedByStaysTheSortWhenMoreRowsArrive()
    {
        var roots = new SourceCache<CompositeItemModel<EntityId>, EntityId>(static model => model.Key);
        var adapter = new DatedAdapter(Provider, roots.Connect());
        using var activation = adapter.Activate();

        roots.AddOrUpdate([Row(1, "oldest", daysAgo: 3), Row(2, "middle", daysAgo: 2)]);
        await EventuallyOnUi(() => NamesInGridOrder(adapter).Should().Equal("middle", "oldest"));

        var installed = adapter.Source.Value.Columns.Single(column => column.Header as string == "Installed");
        adapter.Source.Value.SortBy(installed, ListSortDirection.Ascending);

        roots.AddOrUpdate(Row(3, "newest", daysAgo: 1));
        await EventuallyOnUi(() => NamesInGridOrder(adapter).Should().Equal("oldest", "middle", "newest"));
    }
}
