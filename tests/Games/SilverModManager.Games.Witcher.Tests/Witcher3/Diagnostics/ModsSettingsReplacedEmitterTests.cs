using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Diagnostics;

public class ModsSettingsReplacedEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<ModsSettingsReplacedEmitterTests, Witcher3Game, ModsSettingsReplacedEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddWitcherGames()
            .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private static SortItemKey<string> Key(string folder) => new(folder.ToLowerInvariant());

    private WitcherModSortOrderVariety Variety =>
        Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();

    private async Task<(Loadout.ReadOnly Loadout, SortOrderId SortOrder, LoadoutItemGroupId A, LoadoutItemGroupId B)> TwoMods()
    {
        var loadout = await CreateLoadout();
        var id = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        var a = await AddMod(loadout, "A", 2, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 1, "Mods/modB/b.ws");
        return (loadout, id, a, b);
    }

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, ulong priority, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths) AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), path);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    /// <summary>What an Apply does when the file on disk differs from the one it wrote: ingest it.</summary>
    private async Task IngestFromDisk(Loadout.ReadOnly loadout, string onDisk)
    {
        loadout = Loadout.Load(Connection.Db, loadout.LoadoutId);
        var file = ((ALoadoutSynchronizer)Game.Synchronizer).IntrinsicFiles(loadout)[WitcherConstants.W3ModsSettings];
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [WitcherConstants.W3ModsSettings] = new()
            {
                Previous = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
            },
        };
        using var tx = Connection.BeginTransaction();
        await file.Ingest(new MemoryStream(Encoding.UTF8.GetBytes(onDisk)), loadout, tree, tx);
        await tx.Commit();
    }

    /// <summary>
    /// The manager reconciles a sort order in the background after the mods were added, and a drag built on a database
    /// from before that is refused as stale. Waits until the rows are stored and the database stops moving.
    /// </summary>
    private async Task SortOrderSettled(SortOrderId id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var stable = 0;
        var last = Connection.Db.BasisTxId;
        while (stable < 4)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The sort order did not settle");
            await Task.Delay(50);
            var db = Connection.Db;
            var stored = SortOrderItem.FindByParentSortOrder(db, id).Count;
            stable = stored >= 2 && db.BasisTxId == last ? stable + 1 : 0;
            last = db.BasisTxId;
        }
    }

    private const string Swapped = "[mod0000_MergedFiles]\nPriority=1\n[modB]\nPriority=2\n[modA]\nPriority=3\n";

    [Fact]
    public async Task ANotice_IsOneCard()
    {
        var (loadout, _, _, _) = await TwoMods();
        await IngestFromDisk(loadout, Swapped);

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        await VerifyDiagnostic(diagnostic);
    }

    [Fact]
    public async Task ANoticeFromIngest_ReachesTheHealthCheckThroughTheGamesEmitters()
    {
        var (loadout, _, _, _) = await TwoMods();
        await IngestFromDisk(loadout, "[modGone]\nPriority=1\nVK=modGone\n");

        var fresh = Loadout.Load(Connection.Db, loadout.LoadoutId);
        var emitters = Game.DiagnosticEmitters.OfType<ModsSettingsReplacedEmitter>().ToArray();
        emitters.Should().ContainSingle("the game lists the emitter, so the card is shown and not just stored");
        var cards = await emitters[0].Diagnose(fresh, CancellationToken.None).ToArrayAsync();
        cards.Should().ContainSingle();
    }

    [Fact]
    public async Task WithNoNotice_ThereIsNoCard()
    {
        var (loadout, _, _, _) = await TwoMods();

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADragClearsTheCard()
    {
        var (loadout, id, _, _) = await TwoMods();
        await IngestFromDisk(loadout, Swapped);
        await SortOrderSettled(id);

        await Variety.MoveItems(id, [Key("modB")], Key("modA"), TargetRelativePosition.BeforeTarget, Connection.Db);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ASwitchClearsTheCard()
    {
        var (loadout, id, _, _) = await TwoMods();
        await IngestFromDisk(loadout, Swapped);

        await Variety.SetEnabled(id, Key("modA"), false);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AReconcileKeepsTheCard()
    {
        var (loadout, _, _, _) = await TwoMods();
        await IngestFromDisk(loadout, Swapped);

        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);

        (await GetAllDiagnostics(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task ACollectionImportKeepsTheCard()
    {
        var (loadout, id, a, b) = await TwoMods();
        await IngestFromDisk(loadout, Swapped);

        using (var tx = Connection.BeginTransaction())
        {
            Variety.SeedFromGroupOrder(id, [b, a], tx, Connection.Db);
            await tx.Commit();
        }

        (await GetAllDiagnostics(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task ANoticeOfAnotherLoadout_IsNotACard()
    {
        var (loadout, _, _, _) = await TwoMods();
        await IngestFromDisk(loadout, Swapped);
        var other = await CreateLoadout();

        await ShouldHaveNoDiagnostics(other);
    }
}
