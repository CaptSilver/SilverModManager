using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests;

public class WabbajackModlistLockTests(ITestOutputHelper helper) : InstallTestBase<WabbajackModlistLockTests>(helper)
{
    [Fact]
    public async Task AModlistGroupSaysTheListDecidesWhatItContains()
    {
        var loadout = await CreateLoadout();
        var list = await AddCollection(loadout, "Living Skyrim", modlist: true);

        ServiceProvider.GetRequiredService<ICollectionOwnership>().WhyLocked(CollectionGroup.Load(Connection.Db, list))
            .Should().Be("Living Skyrim is a Wabbajack modlist; it decides what it contains");
    }

    [Fact]
    public async Task AUserCollectionIsStillOwnedByTheUser()
    {
        var loadout = await CreateLoadout();
        var mine = await AddCollection(loadout, "Mine", modlist: false);

        ServiceProvider.GetRequiredService<ICollectionOwnership>().WhyLocked(CollectionGroup.Load(Connection.Db, mine))
            .Should().BeNull();
    }

    private async Task<EntityId> AddCollection(LoadoutId loadout, string name, bool modlist)
    {
        using var tx = Connection.BeginTransaction();
        CollectionGroup.New Collection(EntityId id) => new(tx, id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        if (modlist)
        {
            var group = new WabbajackModlistLoadoutGroup.New(tx, out var id) { ModlistId = EntityId.From(0x6000), CollectionGroup = Collection(id) };
            return (await tx.Commit()).Remap(group).Id;
        }

        var plain = Collection(tx.TempId());
        return (await tx.Commit()).Remap(plain).Id;
    }
}
