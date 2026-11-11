using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Pages.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// Deleting an archive a Wabbajack list's mod is linked to unlinks the mod and keeps it, so the confirmation lists it
/// as kept by the list; an archive a user mod is linked to is listed as used by the loadout, as before.
/// </summary>
public class LibraryItemDeleteWarningDetectorTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<LibraryItem.ReadOnly> AddItem(string name)
    {
        using var tx = Connection.BeginTransaction();
        var item = new LibraryItem.New(tx) { Name = name };
        var result = await tx.Commit();
        return LibraryItem.Load(result.Db, result[item.Id]);
    }

    private async Task LinkGroup(Loadout.ReadOnly loadout, EntityId? parent, string name, LibraryItem.ReadOnly item, bool asWabbajackMod)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                LoadoutId = loadout.LoadoutId,
                Name = name,
            },
        };
        if (parent is { } parentId) tx.Add(id, LoadoutItem.ParentId, parentId);
        if (asWabbajackMod) tx.Add(id, WabbajackModGroup.Folder, name);
        tx.Add(id, LibraryLinkedLoadoutItem.LibraryItemId, item.Id);
        await tx.Commit();
    }

    [Fact]
    public async Task AnArchiveAModlistsModLinksIsKeptByTheListAndOneAUserModLinksIsUsedByTheLoadout()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "delete-warning-" + Guid.NewGuid().ToString("N"));
        var list = await WabbajackGroupSeed.AddList(Connection, "1", name: "Yggdrasil " + Guid.NewGuid().ToString("N"));
        var listGroup = await WabbajackGroupSeed.AddGroup(Connection, list.Id, loadout.LoadoutId, list.Name);
        var listArchive = await AddItem("A");
        var userArchive = await AddItem("B");
        await LinkGroup(loadout, listGroup, "A", listArchive, asWabbajackMod: true);
        await LinkGroup(loadout, null, "B", userArchive, asWabbajackMod: false);

        var detector = LibraryItemDeleteWarningDetector.Process(Connection, Provider.GetRequiredService<ICollectionOwnership>(), [listArchive, userArchive]);

        detector.ItemsInLoadouts.Select(item => item.Id).Should().Equal(userArchive.Id);
        detector.LoadoutsUsed.Should().ContainSingle();
        var kept = detector.KeptByCollections.Should().ContainSingle().Subject;
        kept.CollectionName.Should().Be(list.Name);
        kept.ItemNames.Should().Equal("A");
    }
}
