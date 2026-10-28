using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Collections;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.Order;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Order;

/// <summary>
/// A Witcher 3 loadout's Load Order, shared as a collection and installed into a fresh loadout. The game has no sink of
/// its own, so the order arrives through the default priority sink, which writes the rows the Load Order page is a view over.
/// </summary>
public class Witcher3CollectionOrderRoundTripTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3CollectionOrderRoundTripTests, Witcher3Game>(helper)
{
    private const string Merged = "mod0000_MergedFiles";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static readonly (string Name, string Folder, uint ModId, uint FileId)[] Mods =
    [
        ("Mod A", "modA", 101, 1001),
        ("Mod B", "modB", 102, 1002),
        ("Mod C", "modC", 103, 1003),
    ];

    private static readonly Witcher3VortexOrderFormat Format = new();

    private WitcherModSortOrderVariety Variety => new(ServiceProvider, Game);

    private record Built(Loadout.ReadOnly Loadout, LoadoutItemGroupId Container, Dictionary<string, EntityId> Items, Dictionary<string, LoadoutItemGroupId> Groups);

    private static string ArchiveOf(string name) => $"{name}-{Mods.Single(m => m.Name == name).ModId}-1-0-1700000000.zip";

    /// <summary>A "pack" group with one Nexus-linked child per mod, each holding a script under its own Mods folder. Reusing <paramref name="items"/> installs the same downloads.</summary>
    private async Task<Built> Build(string pack, Dictionary<string, ulong> priorities, Dictionary<string, EntityId>? items = null)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, pack);
        var made = new Dictionary<string, EntityId>();
        var groups = new Dictionary<string, LoadoutItemGroupId>();
        foreach (var (name, folder, modId, fileId) in Mods)
        {
            made[name] = items?[name] ?? ShareableMods.AddNexusItem(tx, name, Witcher3Game.NexusModsGameId.Value, GameDomain.From("witcher3"), modId, fileId, ArchiveOf(name));
            var group = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, name, made[name]);
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Mods/{folder}/content/scripts/game/{folder}.ws"));
            ShareableMods.SetPriority(tx, loadout.LoadoutId, group, priorities[name]);
            groups[name] = group;
        }
        var result = await tx.Commit();
        return new Built(
            Refresh(loadout),
            LoadoutItemGroupId.From(result[container.Value]),
            items ?? made.ToDictionary(pair => pair.Key, pair => result[pair.Value]),
            groups.ToDictionary(pair => pair.Key, pair => LoadoutItemGroupId.From(result[pair.Value.Value])));
    }

    private async Task<SortOrderId> Reconcile(Loadout.ReadOnly loadout)
    {
        var variety = Variety;
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(id);
        return id;
    }

    private string[] Page(SortOrderId id) => Variety.GetSortOrderItems(id, Connection.Db).Select(item => item.DisplayName).ToArray();

    /// <summary>Drags the shared pack to winner-first modC, modA, modB on the Load Order page.</summary>
    private async Task<(Built Shared, CollectionRoot Manifest)> SharedPack()
    {
        var shared = await Build("Shared pack", new() { ["Mod A"] = 1, ["Mod B"] = 2, ["Mod C"] = 3 });
        var id = await Reconcile(shared.Loadout);
        await Variety.MoveItems(id, [WitcherModFolders.KeyFor("modA")], WitcherModFolders.KeyFor("modB"), TargetRelativePosition.BeforeTarget, Connection.Db);
        Page(id).Should().Equal(Merged, "modC", "modA", "modB");
        return (shared, await Share(shared.Container));
    }

    private async Task<CollectionRoot> Share(LoadoutItemGroupId container)
    {
        using var tx = Connection.BeginTransaction();
        var author = new User.New(tx) { Name = "Sharer", NexusId = 99 };
        var result = await tx.Commit();
        return CollectionCreator.LoadoutItemGroupToCollectionManifest(
            LoadoutItemGroup.Load(Connection.Db, container),
            new FixedGameDomains(GameDomain.From("witcher3"), Witcher3Game.NexusModsGameId.Value),
            User.Load(result.Db, result[author.Id]),
            VanityVersion.From("1.0.0"));
    }

    /// <summary>Installs <paramref name="manifest"/>'s order into a fresh loadout of the same downloads, installed with modA winning.</summary>
    private async Task<(Built Fresh, SortOrderId SortOrder)> Install(Built shared, CollectionRoot manifest)
    {
        var fresh = await Build("Installed pack", new() { ["Mod A"] = 3, ["Mod B"] = 2, ["Mod C"] = 1 }, shared.Items);
        var sortOrder = await Reconcile(fresh.Loadout);
        // The downloads start in the order they were installed in.
        Page(sortOrder).Should().Equal(Merged, "modA", "modB", "modC");

        var order = CollectionOrderReader.Read(Connection.Db, Game, manifest, fresh.Container);
        using var tx = Connection.BeginTransaction();
        new ModOrderImporter(ServiceProvider, Connection, NullLogger<ModOrderImporter>.Instance)
            .Import(Refresh(fresh.Loadout), order, new ModOrderSource("collection Shared pack", fresh.Container, manifest), tx);
        await tx.Commit();
        return (fresh, sortOrder);
    }

    private static CollectionRoot With(CollectionRoot root, JsonElement? loadOrder, bool keepOurs) => new()
    {
        Info = root.Info,
        Mods = root.Mods,
        ModRules = root.ModRules,
        LoadOrder = loadOrder,
        SilverLoadOrder = keepOurs ? root.SilverLoadOrder : null,
    };

    [Fact]
    public async Task RoundTripRestoresThePageOrderOnAFreshLoadout()
    {
        var (shared, manifest) = await SharedPack();

        manifest.SilverLoadOrder.Should().NotBeNull();
        manifest.LoadOrder.Should().NotBeNull("the format wrote the game's Vortex key beside ours");
        var (fresh, sortOrder) = await Install(shared, manifest);

        Page(sortOrder).Should().Equal(Merged, "modC", "modA", "modB");
        ModOrderDroppedEntry.FindByLoadout(Connection.Db, fresh.Loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task OurKeyWinsOverLoadOrder()
    {
        var (shared, manifest) = await SharedPack();
        var groups = LoadoutItemGroup.Load(Connection.Db, shared.Container).Children.OfTypeLoadoutItemGroup().ToDictionary(g => g.AsLoadoutItem().Name);
        var disagreeing = Format.Write(shared.Loadout, [groups["Mod B"], groups["Mod A"], groups["Mod C"]])!.Value;

        var (_, sortOrder) = await Install(shared, With(manifest, disagreeing, keepOurs: true));

        Page(sortOrder).Should().Equal(Merged, "modC", "modA", "modB");
    }

    [Fact]
    public async Task LoadOrderAloneIsUsedWhenOurKeyIsAbsent()
    {
        var (shared, manifest) = await SharedPack();
        var array = JsonNode.Parse(manifest.LoadOrder!.Value.GetRawText())!.AsArray();
        array.Add(new JsonObject
        {
            ["id"] = "modGhost", ["name"] = "modGhost", ["enabled"] = true, ["modId"] = "Ghost-999-1-0-1700000009", ["locked"] = false,
            ["data"] = new JsonObject { ["prefix"] = 99 },
        });

        var (fresh, sortOrder) = await Install(shared, With(manifest, JsonSerializer.SerializeToElement(array), keepOurs: false));

        Page(sortOrder).Should().Equal(Merged, "modC", "modA", "modB");
        var dropped = ModOrderDroppedEntry.FindByLoadout(Connection.Db, fresh.Loadout.LoadoutId).Should().ContainSingle().Which;
        dropped.Item.Should().Be("modGhost");
        dropped.Page.Should().Be("Load Order");

        var cards = new List<Diagnostic>();
        await foreach (var card in new ModOrderDroppedEmitter().Diagnose(Loadout.Load(Connection.Db, fresh.Loadout.LoadoutId), FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            cards.Add(card);
        cards.Should().ContainSingle().Which.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("modGhost");
    }

    [Fact]
    public async Task SeedingLeavesThePageOnPriority()
    {
        var (shared, manifest) = await SharedPack();
        var (fresh, sortOrder) = await Install(shared, manifest);

        // The import seeded the stored indexes from the order. Overwrite them with the opposite order: the page reads priorities.
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var row in SortOrderItem.FindByParentSortOrder(Connection.Db, sortOrder))
                tx.Add(row.Id, SortOrderItem.SortIndex, 100 - row.SortIndex);
            await tx.Commit();
        }

        Page(sortOrder).Should().Equal(Merged, "modC", "modA", "modB");
    }
}
