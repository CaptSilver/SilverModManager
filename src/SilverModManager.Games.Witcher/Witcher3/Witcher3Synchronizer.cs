using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.Witcher.Witcher3.Menu;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3;

public sealed class Witcher3Synchronizer(IServiceProvider provider, IGame game) : WitcherSynchronizer(provider, game)
{
    private readonly ModsSettingsFile _modsSettings = new(game, provider.GetRequiredService<ILogger<ModsSettingsFile>>());

    // Built on first use: the engine is registered by the host and has no business being resolved while the game is created.
    private readonly Lazy<MergeSynchronizerPart> _merge = new(() => CreateMergePart(provider, game));

    // The files in Documents/The Witcher 3 the loadout writes. The game and other tools put saves, screenshots, logs and
    // backups in the same folder, and none of that belongs to the loadout, so the folder is an allow-list.
    private static readonly HashSet<GamePath> ManagedDocuments =
        [WitcherConstants.W3ModsSettings, .. WitcherConstants.W3SettingsFiles.Select(file => new GamePath(WitcherConstants.WitcherDocuments, file))];

    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => path.LocationId == WitcherConstants.WitcherDocuments && !ManagedDocuments.Contains(path));

    // INI tweaks (a menu mod's *.part.txt) live in the loadout to be merged into the game's settings; the folder
    // means nothing to the game, so none of it is written.
    protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree)
    {
        GameInis.LeaveDisownedInisOnDisk(Inis, tree);
        GameInis.KeepTweaksOffDisk(tree);
    }

    private IGameIniFiles Inis => (IGameIniFiles)game;

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var files = _merge.Value.IntrinsicFiles(loadout, Logger);
        files[WitcherConstants.W3ModsSettings] = _modsSettings;
        foreach (var file in GameInis.IntrinsicFiles(Inis, loadout, provider))
            files[file.Path] = file;

        // The lists only mean something where the game's menu folder is; a loadout on an install without it gets none.
        if (loadout.IsValid() && loadout.InstallationInstance.Locations.ToAbsolutePath(WitcherConstants.W3MenuConfigFolder).DirectoryExists())
        {
            foreach (var list in new[] { new MenuFileListFile(dx12: true), new MenuFileListFile(dx12: false) })
                files[list.Path] = list;
        }
        return files;
    }

    // Plans first, so a merge that fails is known before the merged files are listed and leaves its path to load order
    // instead of refusing the first apply that meets it.
    protected override Task PreflightLoadout(Loadout.ReadOnly loadout)
    {
        _ = _merge.Value.PlanOutputs(loadout);
        return base.PreflightLoadout(loadout);
    }

    protected override Task PreflightActions(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly? loadout)
    {
        if (loadout is { } current) _merge.Value.CheckOutputs(tree, current);
        return base.PreflightActions(tree, loadout);
    }

    private static MergeSynchronizerPart CreateMergePart(IServiceProvider provider, IGame game)
    {
        var engine = provider.GetRequiredService<MergeEngines>().For(game)
                     ?? throw new InvalidOperationException($"No merge engine serves {game.DisplayName}, so its scripts cannot be merged");
        return new MergeSynchronizerPart(engine, provider.GetRequiredService<ILogger<Witcher3Synchronizer>>());
    }

    /// <summary>
    /// Creates the loadout's Load Order if it has none yet and brings it up to date before the sync tree is built. A
    /// mod installed a moment ago is not reconciled (the manager does that in the background), so without this the
    /// file written from it would miss the folder, and <see cref="ModsSettingsFile.Ingest"/>, which runs in this
    /// apply, would find no sort order to hold its note about a mods.settings it finds replaced.
    /// </summary>
    protected override async ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout)
    {
        // Before the tree is built: an install from before the plan wrote targets canonically has its folders merged and its rows
        // respelled together, so the recorded spelling is always one the disk has.
        Witcher3FolderCase.MergeFoldersOnDisk(loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty)), Logger);
        loadout = await Witcher3FolderCase.Repair(provider.GetRequiredService<IConnection>(), loadout);
        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        loadout = await GameInis.Prepare(Inis, loadout.Rebase(), provider);

        // Plan again on the revision the tree is built from, so the window between the preflight's plan and the
        // reconcile above cannot leave the listing and the plan disagreeing.
        _ = _merge.Value.PlanOutputs(loadout);
        return loadout;
    }
}
