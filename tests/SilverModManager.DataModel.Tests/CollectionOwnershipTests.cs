using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using DynamicData.Kernel;
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

    [Fact]
    public async Task ANexusCollectionDoesNotOwnItsMods()
    {
        var loadout = await CreateLoadout();
        var nexus = await AddNexusCollection(loadout, "Witchcore", collectionId: 1, revisionId: 11, childCount: 1);
        var child = LoadoutItem.FindByParent(Connection.Db, nexus.Id).Single();
        var link = LibraryLinkedLoadoutItem.Load(Connection.Db, child.Id);

        Ownership.OwnsItsMods(CollectionGroup.Load(Connection.Db, nexus.Id)).Should().BeFalse();
        Ownership.OwnsLink(Connection.Db, link).Should().BeFalse();
        Ownership.WhyLocked(Connection.Db, link).Should().Be("Witchcore is a Nexus collection; it decides what it contains");
    }

    [Fact]
    public async Task OwnsLinkAsksTheNearestCollection()
    {
        var loadout = await CreateLoadout();
        var collection = await AddUserCollection(loadout, "Owned");
        var ownership = new CollectionOwnership([new StubLock(null, owns: true)]);

        // a link under a plain group that sits under the collection
        EntityId plain;
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "plain", LoadoutItemGroupId.From(collection.Value));
            plain = (await tx.Commit())[group];
        }
        var nested = await InstallLink(loadout, "nested_archive.zip", LoadoutItemGroupId.From(plain.Value));
        ownership.OwnsLink(Connection.Db, nested).Should().BeTrue();

        // a link in the loadout root has no collection and no parent at all
        var root = await AddRootLink(loadout);
        ownership.OwnsLink(Connection.Db, root).Should().BeFalse();
        ownership.WhyLocked(Connection.Db, root).Should().BeNull();
    }

    [Fact]
    public void OwnsItsModsIsTrueWhenAnyLockSaysSo()
    {
        var group = default(CollectionGroup.ReadOnly);

        new CollectionOwnership([new StubLock(null), new StubLock("x", owns: true)]).OwnsItsMods(group).Should().BeTrue();
        new CollectionOwnership([new StubLock(null), new StubLock("x")]).OwnsItsMods(group).Should().BeFalse();
    }

    private async Task<LibraryLinkedLoadoutItem.ReadOnly> InstallLink(LoadoutId loadout, string archive, Optional<LoadoutItemGroupId> parent)
    {
        var path = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(archive);
        var item = (await ServiceProvider.GetRequiredService<ILibraryService>()
            .AddLocalFile(path, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();
        await LoadoutManager.InstallItem(item, loadout, parent: parent);
        return LibraryLinkedLoadoutItem.FindByLibraryItem(Connection.Db, item.Id).Single();
    }

    /// <summary>A link with no parent at all; an install always lands in a collection, but groups made elsewhere need not.</summary>
    private async Task<LibraryLinkedLoadoutItem.ReadOnly> AddRootLink(LoadoutId loadout)
    {
        var path = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("nested_game_archive.zip");
        var item = (await ServiceProvider.GetRequiredService<ILibraryService>()
            .AddLocalFile(path, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();

        using var tx = Connection.BeginTransaction();
        var link = new LibraryLinkedLoadoutItem.New(tx, out var id)
        {
            LibraryItemId = item.Id,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "root link", LoadoutId = loadout },
            },
        };
        return (await tx.Commit()).Remap(link);
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

    private sealed class StubLock(string? reason, bool owns = false) : ICollectionLock
    {
        public string? WhyLocked(CollectionGroup.ReadOnly collection) => reason;
        public bool OwnsItsMods(CollectionGroup.ReadOnly collection) => owns;
    }
}
