using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Collections;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.FromSoft.Tests.LoadOrder;

/// <summary>
/// Elden Ring has no order sink or Vortex key of its own, so a shared collection's order reaches the family's loadout
/// through the default priority sink alone.
/// </summary>
public class EldenRingCollectionOrderRoundTripTests(ITestOutputHelper helper) : AEldenRingTest<EldenRingCollectionOrderRoundTripTests>(helper)
{
    private static readonly (string Name, UInt128 Md5)[] Mods = [("alpha.zip", 1), ("bravo.zip", 2), ("charlie.zip", 3), ("delta.zip", 4)];

    [Fact]
    public async Task RoundTripRestoresTheOrderOnAFreshLoadout()
    {
        // Winner first: charlie, alpha, delta, bravo.
        var priorities = new Dictionary<string, ulong> { ["alpha.zip"] = 3, ["bravo.zip"] = 1, ["charlie.zip"] = 4, ["delta.zip"] = 2 };
        var shared = await Build("Shared pack", priorities);
        var manifest = await Share(shared.Container);

        // The same downloads in a fresh loadout, installed in a different order from the shared one.
        var fresh = await Build("Installed pack", Mods.ToDictionary(mod => mod.Name, mod => (ulong)(Mods.ToList().FindIndex(m => m.Name == mod.Name) + 1)), shared.Files);
        var order = CollectionOrderReader.Read(Connection.Db, Game, manifest, fresh.Container);
        using (var tx = Connection.BeginTransaction())
        {
            new ModOrderImporter(ServiceProvider, Connection, NullLogger<ModOrderImporter>.Instance)
                .Import(Refresh(fresh.Loadout), order, new ModOrderSource("collection Shared pack", fresh.Container, manifest), tx);
            await tx.Commit();
        }

        Order(fresh.Loadout.LoadoutId, fresh.Container).Should().Equal("charlie.zip", "alpha.zip", "delta.zip", "bravo.zip");
        ModOrderDroppedEntry.FindByLoadout(Connection.Db, fresh.Loadout.LoadoutId).Should().BeEmpty();
    }

    private record Built(Loadout.ReadOnly Loadout, LoadoutItemGroupId Container, Dictionary<string, EntityId> Files);

    /// <summary>A "pack" group with one downloaded-file child per mod. Reusing <paramref name="files"/> makes the children the same downloads.</summary>
    private async Task<Built> Build(string pack, Dictionary<string, ulong> priorities, Dictionary<string, EntityId>? files = null)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, pack);
        var made = new Dictionary<string, EntityId>();
        var groups = new List<LoadoutItemGroupId>();
        foreach (var (name, md5) in Mods)
        {
            made[name] = files?[name] ?? ShareableMods.AddDownloadedFile(tx, name, Md5Value.From(md5));
            var group = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, name, made[name]);
            ShareableMods.SetPriority(tx, loadout.LoadoutId, group, priorities[name]);
        }
        var result = await tx.Commit();
        return new Built(loadout, LoadoutItemGroupId.From(result[container].Value), files ?? made.ToDictionary(pair => pair.Key, pair => result[pair.Value]));
    }

    private async Task<CollectionRoot> Share(LoadoutItemGroupId container)
    {
        using var tx = Connection.BeginTransaction();
        var author = new User.New(tx) { Name = "Sharer", NexusId = 99 };
        var result = await tx.Commit();
        return CollectionCreator.LoadoutItemGroupToCollectionManifest(
            LoadoutItemGroup.Load(Connection.Db, container),
            new FixedGameDomains(GameDomain.From("eldenring"), EldenRingGame.NexusModsGameId.Value),
            User.Load(result.Db, result[author.Id]),
            VanityVersion.From("1.0.0"));
    }

    private string[] Order(LoadoutId loadout, LoadoutItemGroupId container)
    {
        var db = Connection.Db;
        var priorities = LoadoutItemGroupPriority.FindByLoadout(db, loadout).ToDictionary(row => row.TargetId, row => row.Priority.Value);
        return LoadoutItemGroup.Load(db, container).Children.OfTypeLoadoutItemGroup()
            .OrderByDescending(child => priorities[child.LoadoutItemGroupId])
            .Select(child => child.AsLoadoutItem().Name)
            .ToArray();
    }
}
