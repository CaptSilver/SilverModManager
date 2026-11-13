using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.Larian.Engine.Modsettings;

namespace SilverModManager.Games.Larian.Engine;

public class LarianSynchronizer : ALoadoutSynchronizer
{
    private static GamePath GameFolder => new(LocationId.Game, "");
    private static GamePath PlayerProfiles => new(LarianConstants.PlayerProfilesLocationId, "");

    private readonly LarianGameProfile _profile;
    private readonly ILarianGame _game;
    private readonly LarianPakModules _modules;
    private readonly ModsettingsFile? _modsettings;
    private readonly GamePath? _modsettingsPath;

    public LarianSynchronizer(IServiceProvider provider, ILarianGame game, LarianPakModules modules) : base(provider)
    {
        _game = game;
        _profile = game.Profile;
        _modules = modules;

        if (_profile.Schema is { } schema)
        {
            if (_profile.Profiles is not LarianProfileLayout.Fixed)
                throw new NotSupportedException($"{_profile.GameName} keeps its mod list in a folder per player profile, which this synchronizer does not resolve yet");

            _modsettingsPath = new GamePath(LarianConstants.PlayerProfilesLocationId, "modsettings.lsx");
            _modsettings = new ModsettingsFile(game, _modules, schema, _modsettingsPath.Value, provider.GetRequiredService<ILogger<ModsettingsFile>>());
        }

        // Everything inside the player profiles folder is the game's (saves, settings) and stays out of the loadout,
        // except the mod list the app writes.
        GameSpecificPathFilter = Abstractions.Loadouts.Synchronizers.GamePathFilters.Create(path =>
            path.InFolder(PlayerProfiles) && !IsModsettings(path));
    }

    private bool IsModsettings(GamePath path) => _modsettingsPath is { } modsettings && path.Equals(modsettings);

    // The native build keeps these under XDG_DATA_HOME, the Windows build in its Proton prefix, so a game that was
    // managed in the prefix finds them in a new folder once it is recognised as native.
    protected override IReadOnlySet<LocationId> RelocatableLocations { get; } =
        new HashSet<LocationId> { LarianConstants.ModsLocationId, LarianConstants.PlayerProfilesLocationId };

    protected override IGamePathFilter GameSpecificPathFilter { get; }

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_game.DoFullGameBackup) return false;
        return path.InFolder(GameFolder) || (path.InFolder(PlayerProfiles) && !IsModsettings(path));
    }

    protected override IGameInstalledContentRecognizer? GameInstalledContent => _profile.GameInstalledRecognizer;

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

    /// <summary>
    /// An entry that differs from the stored one in any field. An item with no entry this time keeps the stored
    /// one, which is what <see cref="WriteGameInstalledItemExtras"/> does too.
    /// </summary>
    protected override bool GameInstalledExtrasDiffer(IDb db, EntityId itemId, GameInstalledContent content)
    {
        if (content.Extra is not ModsettingsDocument.ModuleEntry entry) return false;

        var item = GameInstalledItem.Load(db, itemId);
        if (!item.IsValid() || !item.TryGetAsBg3ModuleEntry(out var stored)) return true;

        return !Same(stored.Folder, entry.Folder)
            || !Same(stored.Uuid, entry.Uuid)
            || !Same(stored.Version64, entry.Version64)
            || !Same(stored.PublishHandle, entry.PublishHandle)
            || !Same(stored.Md5, entry.Md5)
            || !Same(stored.ModuleName, entry.Name);
    }

    private static bool Same(Optional<string> stored, string value) => stored.HasValue && stored.Value == value;

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) =>
        _modsettings is null ? new() : new() { [_modsettings.Path] = _modsettings };

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

        // A game the app writes no mod list for has no pak order to work out.
        if (_modsettings is not { } modsettings) return loadout;

        await _game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        loadout = loadout.Rebase();

        var variety = _game.SortOrderManager.GetSortOrderVarieties().OfType<ILarianPakSortOrderVariety>().Single();
        var sortOrderId = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        loadout = loadout.Rebase();

        // The note describes the Apply that wrote it. A later Apply either finds the file edited again, which
        // stages a fresh note while the tree is processed, or finds it untouched, which leaves none.
        await variety.ClearReplacedGameEdit(sortOrderId);
        loadout = loadout.Rebase();

        if (!LarianPakSeeder.IsSeeded(loadout.Db, sortOrderId))
        {
            var seedCandidates = await _modules.Candidates(loadout, variety, sortOrderId);
            var notYetInLoadout = _modules.PaksOnDiskNotIn(loadout, seedCandidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            await LarianPakSeeder.Seed(variety, Connection, sortOrderId, seedCandidates, notYetInLoadout, modsettings.ReadDocument(loadout));
            loadout = loadout.Rebase();
        }

        var candidates = await _modules.Candidates(loadout, variety, sortOrderId);

        // Before the plan replaces the list the app last wrote, which is what the file is compared against.
        if (!LarianModListNotices.IsHeld(loadout.Db, sortOrderId) && modsettings.GameThrewTheListAway(loadout, candidates, LarianModListNotices.LastWritten(loadout.Db, sortOrderId)))
        {
            using var resetTx = Connection.BeginTransaction();
            LarianModListNotices.StageReset(loadout.Db, sortOrderId, LarianModListNotices.NewlyActive(loadout.Db, sortOrderId), DateTimeOffset.UtcNow, resetTx);
            await resetTx.Commit();
            loadout = loadout.Rebase();
        }

        // Once held, the list is whatever the game's file names, so the comparison after the write finds nothing to replace.
        var names = LarianModListNotices.IsHeld(loadout.Db, sortOrderId)
            ? modsettings.PaksListedOnDisk(loadout, candidates)
            : LarianModListPlan.Build(candidates).Names;

        using var tx = Connection.BeginTransaction();
        var planChanged = LarianModListNotices.StagePlan(loadout.Db, sortOrderId, names, DateTimeOffset.UtcNow, tx);
        var overrides = OverridesRowsForModsettings(loadout, modsettings.Path).ToList();
        foreach (var row in overrides)
            tx.Delete(row, recursive: false);

        // An Apply that changes nothing must not write a transaction.
        if (!planChanged && overrides.Count == 0) return loadout;
        await tx.Commit();

        return loadout.Rebase();
    }

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> OverridesRowsForModsettings(Loadout.ReadOnly loadout, GamePath modsettingsPath) =>
        LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Where(item => item.TargetPath == modsettingsPath);
}
