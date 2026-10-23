using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Larian.BaldursGate3.GameInstalled;
using NexusMods.Games.Larian.BaldursGate3.ModList;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.Larian.BaldursGate3;

public class BaldursGate3Synchronizer : ALoadoutSynchronizer
{
    private readonly BaldursGate3Settings _settings;

    private static GamePath GameFolder => new(LocationId.Game, "");
    private static GamePath PublicPlayerProfiles => new(Bg3Constants.PlayerProfilesLocationId, "");

    private readonly Bg3InGameModRecognizer _inGameModRecognizer = new();
    private readonly IGame _game;
    private readonly Bg3PakModules _modules;
    private readonly ModsettingsFile _modsettings;

    public BaldursGate3Synchronizer(IServiceProvider provider, IGame game) : base(provider)
    {
        var settingsManager = provider.GetRequiredService<ISettingsManager>();
        _settings = settingsManager.Get<BaldursGate3Settings>();
        _game = game;
        _modules = new Bg3PakModules(provider);
        _modsettings = new ModsettingsFile(game, _modules, provider.GetRequiredService<ILogger<ModsettingsFile>>());
    }

    protected override IGamePathFilter GameSpecificPathFilter { get; } = Abstractions.Loadouts.Synchronizers.GamePathFilters.Create(path =>
    {
        // ignore all files inside the public player profiles directory except the modsettings.lsx file
        if (path.InFolder(PublicPlayerProfiles)) return !path.Equals(ModsettingsFile.FilePath);
        return false;
    });

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup) return false;
        return path.InFolder(GameFolder) || (path.InFolder(PublicPlayerProfiles) && path.Path != ModsettingsFile.FilePath.Path);
    }

    protected override IGameInstalledContentRecognizer? GameInstalledContent => _inGameModRecognizer;

    /// <summary>
    /// Stores the <c>modsettings.lsx</c> entry of a pak the game installed. A pak the game switched off loses
    /// its entry there, and the writer needs the stored copy to bring it back, so an item without an entry this
    /// time keeps the one it has. Re-adding an unchanged value adds no datom.
    /// </summary>
    protected override void WriteGameInstalledItemExtras(ITransaction tx, EntityId itemId, GameInstalledContent content)
    {
        if (content.Extra is not ModsettingsDocument.ModuleEntry entry) return;

        tx.Add(itemId, Bg3ModuleEntry.Folder, entry.Folder);
        tx.Add(itemId, Bg3ModuleEntry.Uuid, entry.Uuid);
        tx.Add(itemId, Bg3ModuleEntry.Version64, entry.Version64);
        tx.Add(itemId, Bg3ModuleEntry.PublishHandle, entry.PublishHandle);
        tx.Add(itemId, Bg3ModuleEntry.Md5, entry.Md5);
        tx.Add(itemId, Bg3ModuleEntry.ModuleName, entry.Name);
    }

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) =>
        new() { [ModsettingsFile.FilePath] = _modsettings };

    /// <summary>
    /// Works out the mod list before the sync tree is built: brings the pak order up to date, fills it from the
    /// game's own list the first time, and stores the paks the list will name. Writing the file has no
    /// transaction of its own, so it reads what is stored here. The same transaction drops the Overrides row an
    /// earlier version made for <c>modsettings.lsx</c>: the generated file shadows that path from this sync on, and
    /// dropping the row sooner would make the sync delete the game's file.
    /// </summary>
    protected override async ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout)
    {
        loadout = await base.PrepareLoadoutForSync(loadout);

        await _game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        loadout = loadout.Rebase();

        var variety = _game.SortOrderManager.GetSortOrderVarieties().OfType<IBg3PakSortOrderVariety>().Single();
        var sortOrderId = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        loadout = loadout.Rebase();

        // The note describes the Apply that wrote it. A later Apply either finds the file edited again, which
        // stages a fresh note while the tree is processed, or finds it untouched, which leaves none.
        await variety.ClearReplacedGameEdit(sortOrderId);
        loadout = loadout.Rebase();

        if (!Bg3PakSeeder.IsSeeded(loadout.Db, sortOrderId))
        {
            var candidates = await _modules.Candidates(loadout, variety, sortOrderId);
            var notYetInLoadout = _modules.PaksOnDiskNotIn(loadout, candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            await Bg3PakSeeder.Seed(variety, Connection, sortOrderId, candidates, notYetInLoadout, _modsettings.ReadDocument(loadout));
            loadout = loadout.Rebase();
        }

        var plan = Bg3ModListPlan.Build(await _modules.Candidates(loadout, variety, sortOrderId));

        using var tx = Connection.BeginTransaction();
        Bg3ModListNotices.StagePlan(loadout.Db, sortOrderId, plan.Names, DateTimeOffset.UtcNow, tx);
        foreach (var row in OverridesRowsForModsettings(loadout))
            tx.Delete(row, recursive: false);
        await tx.Commit();

        return loadout.Rebase();
    }

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> OverridesRowsForModsettings(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Where(item => item.TargetPath == ModsettingsFile.FilePath);
}
