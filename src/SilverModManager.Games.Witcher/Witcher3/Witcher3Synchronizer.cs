using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3;

public sealed class Witcher3Synchronizer(IServiceProvider provider, IGame game) : WitcherSynchronizer(provider, game)
{
    private readonly ModsSettingsFile _modsSettings = new(game, provider.GetRequiredService<ILogger<ModsSettingsFile>>());

    private static readonly GamePath SavesFolder = new(WitcherConstants.WitcherDocuments, WitcherConstants.W3GameSaves);

    // Saves sit in the same Documents folder as mods.settings, which has to stay managed, so the location can't be
    // filtered whole.
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => path.InFolder(SavesFolder));

    // INI tweaks (a menu mod's *.part.txt) live in the loadout to be merged into the game's settings; the folder
    // means nothing to the game, so none of it is written.
    protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree) => GameInis.KeepTweaksOffDisk(tree);

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) =>
        new() { [WitcherConstants.W3ModsSettings] = _modsSettings };

    /// <summary>
    /// Brings the Load Order up to date before the sync tree is built: a mod installed a moment ago gets its row (the
    /// manager's own reconcile runs in the background), so the file written from it lists the folder.
    /// </summary>
    protected override async ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout)
    {
        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        return loadout.Rebase();
    }
}
