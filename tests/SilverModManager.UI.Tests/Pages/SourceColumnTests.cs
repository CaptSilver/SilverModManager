using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using SilverModManager.App.UI.Pages;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using DynamicData.Kernel;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// Both mod trees have a sortable Source column that says where each row came from.
/// </summary>
public class SourceColumnTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class LoadoutProbe(IServiceProvider provider, LoadoutFilter filter) : LoadoutTreeDataGridAdapter(provider, filter)
    {
        public IColumn<CompositeItemModel<EntityId>>[] Columns() => CreateColumns(viewHierarchical: true);
    }

    private sealed class LibraryProbe(IServiceProvider provider, LibraryFilter filter) : LibraryTreeDataGridAdapter(provider, filter)
    {
        public IColumn<CompositeItemModel<EntityId>>[] Columns() => CreateColumns(viewHierarchical: true);
    }

    private static void ShouldHaveSortableSourceColumn(IColumn<CompositeItemModel<EntityId>>[] columns)
    {
        var column = columns.OfType<CustomTemplateColumn<CompositeItemModel<EntityId>>>()
            .Single(column => (string?)column.Tag == SharedColumns.Source.ColumnTemplateResourceKey);
        column.Header.Should().Be("Source");
        column.Options.CanUserSortColumn.Should().BeTrue();
    }

    [Fact]
    public void TheLoadoutTree_HasASourceColumn()
    {
        var filter = new LoadoutFilter { LoadoutId = LoadoutId.From(EntityId.From(0xBEEF)), CollectionGroupId = Optional<LoadoutItemGroupId>.None };
        ShouldHaveSortableSourceColumn(new LoadoutProbe(Provider, filter).Columns());
    }

    [Fact]
    public void TheLibraryTree_HasASourceColumn()
    {
        var filter = new LibraryFilter(LoadoutId.From(EntityId.From(0xBEEF)), null!);
        ShouldHaveSortableSourceColumn(new LibraryProbe(Provider, filter).Columns());
    }

    [Fact]
    public void SourcesSortAlphabetically_AndARowWithoutOneComesFirst()
    {
        var nexus = new CompositeItemModel<EntityId>(EntityId.From(1));
        SharedColumns.Source.Add(nexus, ModSources.Nexus);
        var thunderstore = new CompositeItemModel<EntityId>(EntityId.From(2));
        SharedColumns.Source.Add(thunderstore, ModSources.Thunderstore);
        var none = new CompositeItemModel<EntityId>(EntityId.From(3));

        SharedColumns.Source.Compare(nexus, thunderstore).Should().BeNegative();
        SharedColumns.Source.Compare(thunderstore, nexus).Should().BePositive();
        SharedColumns.Source.Compare(none, nexus).Should().BeNegative();
    }

    [Fact]
    public void TheSourceNames_AreWhatTheColumnShows()
    {
        ModSources.Nexus.Should().Be("Nexus Mods");
        ModSources.Thunderstore.Should().Be("Thunderstore");
        ModSources.LocalFile.Should().Be("Local file");
        ModSources.Wabbajack.Should().Be("Wabbajack");
        ModSources.Bundled.Should().Be("Bundled");
        ModSources.Game.Should().Be("Game");
    }
}
