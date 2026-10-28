using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3;

public sealed class Witcher3Synchronizer(IServiceProvider provider, IGame game) : WitcherSynchronizer(provider, game)
{
    private readonly ModsSettingsFile _modsSettings = new(game, provider.GetRequiredService<ILogger<ModsSettingsFile>>());

    // Built on first use: the engine is registered by the host and has no business being resolved while the game is created.
    private readonly Lazy<MergeSynchronizerPart> _merge = new(() => CreateMergePart(provider, game));

    private static readonly GamePath SavesFolder = new(WitcherConstants.WitcherDocuments, WitcherConstants.W3GameSaves);

    // Saves sit in the same Documents folder as mods.settings, which has to stay managed, so the location can't be
    // filtered whole.
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => path.InFolder(SavesFolder));

    // INI tweaks (a menu mod's *.part.txt) live in the loadout to be merged into the game's settings; the folder
    // means nothing to the game, so none of it is written.
    protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree) => GameInis.KeepTweaksOffDisk(tree);

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var files = _merge.Value.IntrinsicFiles(loadout, Logger);
        files[WitcherConstants.W3ModsSettings] = _modsSettings;
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
        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        return loadout.Rebase();
    }
}
