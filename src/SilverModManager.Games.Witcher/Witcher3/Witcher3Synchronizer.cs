using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher3;

public sealed class Witcher3Synchronizer(IServiceProvider provider, IGame game) : WitcherSynchronizer(provider, game)
{
    private static readonly GamePath SavesFolder = new(WitcherConstants.WitcherDocuments, WitcherConstants.W3GameSaves);

    // Saves sit in the same Documents folder as mods.settings, which has to stay managed, so the location can't be
    // filtered whole.
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => path.InFolder(SavesFolder));

    // INI tweaks (a menu mod's *.part.txt) live in the loadout to be merged into the game's settings; the folder
    // means nothing to the game, so none of it is written.
    protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree) => GameInis.KeepTweaksOffDisk(tree);
}
