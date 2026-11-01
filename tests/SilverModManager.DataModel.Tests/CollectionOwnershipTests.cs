using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class CollectionOwnershipTests(ITestOutputHelper helper) : ANexusCollectionTest<CollectionOwnershipTests>(helper)
{
    private ICollectionOwnership Ownership => ServiceProvider.GetRequiredService<ICollectionOwnership>();

    [Fact]
    public async Task AUserCollectionIsNotLocked()
    {
        var loadout = await CreateLoadout();
        var mine = await AddUserCollection(loadout, "My own");

        Ownership.WhyLocked(CollectionGroup.Load(Connection.Db, mine)).Should().BeNull();
    }

    [Fact]
    public async Task ANexusCollectionSaysItDecidesWhatItContains()
    {
        var loadout = await CreateLoadout();
        var nexus = await AddNexusCollection(loadout, "Witchcore", collectionId: 1, revisionId: 11, childCount: 1);

        Ownership.WhyLocked(CollectionGroup.Load(Connection.Db, nexus.Id))
            .Should().Be("Witchcore is a Nexus collection; it decides what it contains");
    }

    [Fact]
    public void LocksAreAskedInRegistrationOrderAndTheFirstReasonWins()
    {
        var group = default(CollectionGroup.ReadOnly);
        var ownership = new CollectionOwnership([new StubLock(null), new StubLock("first"), new StubLock("second")]);

        ownership.WhyLocked(group).Should().Be("first");
        new CollectionOwnership([new StubLock(null)]).WhyLocked(group).Should().BeNull();
        new CollectionOwnership([]).WhyLocked(group).Should().BeNull();
    }

    private async Task<EntityId> AddUserCollection(LoadoutId loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var group = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        return (await tx.Commit()).Remap(group).Id;
    }

    private sealed class StubLock(string? reason) : ICollectionLock
    {
        public string? WhyLocked(CollectionGroup.ReadOnly collection) => reason;
    }
}
