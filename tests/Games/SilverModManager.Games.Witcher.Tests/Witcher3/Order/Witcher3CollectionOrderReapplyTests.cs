using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Collections;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Order;

/// <summary>
/// A Witcher 3 collection whose manifest carries only the Vortex <c>loadOrder</c> key, installed while the Vortex install id
/// matching was wrong: every id matched nothing, the order stayed in install order and each entry was listed as left out.
/// Applying the order again has to read the Vortex key through the game's own format and fix both.
/// </summary>
public class Witcher3CollectionOrderReapplyTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3CollectionOrderReapplyTests, Witcher3Game>(helper)
{
    private const string Merged = "mod0000_MergedFiles";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    /// <summary>
    /// Listed winner first, as the manifest's <c>loadOrder</c> will list them. The last one's install id has several numeric
    /// segments, so only the collection's own mod list says which one is the Nexus mod id.
    /// </summary>
    private static readonly (string Name, string Folder, uint ModId, uint FileId, string InstallId)[] Mods =
    [
        ("Mod C", "modC", 103, 1003, "Mod C-103-1-0-1700000000"),
        ("Mod A", "modA", 101, 1001, "Mod A-101-1-0-1700000000"),
        ("Mod B", "modB", 102, 1002, "Mod B-102-1-0-1700000000"),
        ("Beta mod", "modBeta", 123, 1004, "Mod-123-beta-2-rc-1700000000"),
    ];

    private WitcherModSortOrderVariety Variety => new(ServiceProvider, Game);

    private static CollectionRoot Manifest()
    {
        var order = new JsonArray
        {
            Entry(Merged, Merged, Merged, locked: true, prefix: 1),
        };
        for (var i = 0; i < Mods.Length; i++) order.Add(Entry(Mods[i].Folder, Mods[i].Name, Mods[i].InstallId, locked: false, prefix: i + 2));

        return new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("witcher3") },
            Mods = Mods.Select(mod => new Mod
            {
                Name = mod.Name,
                DomainName = GameDomain.From("witcher3"),
                Source = new ModSource
                {
                    Type = ModSourceType.NexusMods,
                    ModId = ModId.From(mod.ModId),
                    FileId = FileId.From(mod.FileId),
                    LogicalFilename = $"{mod.InstallId}.zip",
                },
            }).ToArray(),
            LoadOrder = JsonSerializer.SerializeToElement(order),
        };
    }

    private static JsonObject Entry(string id, string name, string modId, bool locked, int prefix) => new()
    {
        ["id"] = id, ["name"] = name, ["enabled"] = true, ["modId"] = modId, ["locked"] = locked,
        ["data"] = new JsonObject { ["prefix"] = prefix },
    };

    /// <summary>The collection installed in a loadout, with the mods installed in the reverse of the manifest's order.</summary>
    private async Task<(Loadout.ReadOnly Loadout, NexusCollectionLoadoutGroup.ReadOnly Group)> InstalledCollection()
    {
        var (file, revision, _) = await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager).Create(Manifest());
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = new NexusCollectionLoadoutGroup.New(tx, out var id)
        {
            CollectionId = revision.Collection,
            RevisionId = revision,
            LibraryFileId = file,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = revision.Collection.Name, LoadoutId = loadout.LoadoutId },
                },
            },
        };
        var container = LoadoutItemGroupId.From(id);
        for (var i = 0; i < Mods.Length; i++)
        {
            var (name, folder, modId, fileId, installId) = Mods[i];
            var item = ShareableMods.AddNexusItem(tx, name, Witcher3Game.NexusModsGameId.Value, GameDomain.From("witcher3"), modId, fileId, $"{installId}.zip");
            var mod = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, name, item);
            AddFile(tx, loadout.LoadoutId, mod, new GamePath(LocationId.Game, $"Mods/{folder}/content/scripts/game/{folder}.ws"));
            ShareableMods.SetPriority(tx, loadout.LoadoutId, mod, (ulong)(i + 1));
        }

        var result = await tx.Commit();
        return (Refresh(loadout), NexusCollectionLoadoutGroup.Load(result.Db, result[group.Id]));
    }

    private async Task<SortOrderId> Reconcile(Loadout.ReadOnly loadout)
    {
        var variety = Variety;
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(id);
        return id;
    }

    private string[] Page(SortOrderId id) => Variety.GetSortOrderItems(id, Connection.Db).Select(item => item.DisplayName).ToArray();

    /// <summary>What the broken joiner left: the install order stands and every entry of the key is listed as left out.</summary>
    private async Task LeaveWhatTheBrokenJoinerLeft(Loadout.ReadOnly loadout, NexusCollectionLoadoutGroup.ReadOnly group)
    {
        var container = group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId;
        var drops = Mods.Select(mod => new ModOrderDrop(mod.Name, $"no mod in the collection matches '{mod.InstallId}'", "Load Order"));

        using var tx = Connection.BeginTransaction();
        new ModOrderImporter(ServiceProvider, Connection, NullLogger<ModOrderImporter>.Instance)
            .Import(Refresh(loadout), ModOrder.Create([], drops), new ModOrderSource($"collection {group.Collection.Name}", container, Manifest: null), tx);
        await tx.Commit();
    }

    private ModOrderDroppedEntry.ReadOnly[] Rows(LoadoutId loadout) => ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout).ToArray();

    [Fact]
    public async Task ReapplyingReadsTheVortexKeyAndFixesTheOrderAndTheDroppedRows()
    {
        var (loadout, group) = await InstalledCollection();
        var sortOrder = await Reconcile(loadout);
        await LeaveWhatTheBrokenJoinerLeft(loadout, group);

        var manifest = Manifest();
        manifest.SilverLoadOrder.Should().BeNull("only the Vortex key reaches the game's own format");
        Rows(loadout.LoadoutId).Should().HaveCount(Mods.Length);
        Page(sortOrder).Should().NotEqual([Merged, .. Mods.Select(mod => mod.Folder)], "the install order stands until the order is applied again");

        var cards = new List<Diagnostic>();
        await foreach (var card in new ModOrderDroppedEmitter().Diagnose(Loadout.Load(Connection.Db, loadout.LoadoutId), FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            cards.Add(card);
        cards.Should().ContainSingle().Which.Actions.Should().ContainSingle(action => action.Id == DiagnosticActions.ReapplyCollectionOrderId);

        var result = await CollectionOrderReapply.Reapply(ServiceProvider, group);

        result.Dropped.Should().Be(0);
        Rows(loadout.LoadoutId).Should().BeEmpty();

        // The Load Order page is a view over the priorities: the first mod the manifest lists is the winner, with the highest priority.
        Page(sortOrder).Should().Equal([Merged, .. Mods.Select(mod => mod.Folder)]);
        var priorities = LoadoutItemGroup.Load(Connection.Db, group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId).Children.OfTypeLoadoutItemGroup()
            .ToDictionary(child => child.AsLoadoutItem().Name, child => LoadoutItemGroupPriority.FindByTarget(Connection.Db, child.LoadoutItemGroupId).Single().Priority.Value);
        Mods.Select(mod => priorities[mod.Name]).Should().BeInDescendingOrder();
    }
}
