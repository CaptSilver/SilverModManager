using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Pages.Collections;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>A mod row's "open collection" asks the collection's source which page to open.</summary>
public class CollectionOpenFromModRowTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<EntityId> SeedChildOf(LoadoutId loadout, EntityId group)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "A mod", LoadoutId = loadout },
        };
        tx.Add(id, LoadoutItem.Parent, group);
        var result = await tx.Commit();
        return result[id];
    }

    private PageData Opened(LoadoutId loadout, EntityId child)
    {
        var controller = Substitute.For<IWorkspaceController>();
        PageData? opened = null;
        controller.When(c => c.OpenPage(Arg.Any<WorkspaceId>(), Arg.Any<Optional<PageData>>(), Arg.Any<OpenPageBehavior>(), Arg.Any<bool>(), Arg.Any<bool>()))
            .Do(call => opened = call.ArgAt<Optional<PageData>>(1).Value);

        LoadoutViewModel.HandleOpenItemCollectionPage(
            [LoadoutItemId.From(child)], default, loadout, controller, Connection, Provider.GetRequiredService<CollectionProviders>());

        return opened.Should().NotBeNull().And.Subject.As<PageData>();
    }

    [Fact]
    public async Task ANexusCollectionsModRowOpensTheCollectionPageWithNoRevisionId()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Row nexus {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Row collection");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Row collection", revision);
        var child = await SeedChildOf(loadout.LoadoutId, group.Id);

        Opened(loadout.LoadoutId, child).Should().Be(new PageData
        {
            FactoryId = CollectionLoadoutPageFactory.StaticId,
            Context = new CollectionLoadoutPageContext { LoadoutId = loadout.LoadoutId, GroupId = group.AsCollectionGroup().CollectionGroupId },
        });
    }

    [Fact]
    public async Task ALocalCollectionsModRowOpensTheScopedLoadoutPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Row local {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Row local list");
        var child = await SeedChildOf(loadout.LoadoutId, group.Id);

        Opened(loadout.LoadoutId, child).Should().Be(new PageData
        {
            FactoryId = LoadoutPageFactory.StaticId,
            Context = new LoadoutPageContext { LoadoutId = loadout.LoadoutId, GroupScope = group.CollectionGroupId },
        });
    }
}
