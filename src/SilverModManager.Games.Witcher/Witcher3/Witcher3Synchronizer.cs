using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher3;

public sealed class Witcher3Synchronizer(IServiceProvider provider, IGame game) : WitcherSynchronizer(provider, game)
{
    private static readonly GamePath SavesFolder = new(WitcherConstants.WitcherDocuments, WitcherConstants.W3GameSaves);

    // Saves sit in the same Documents folder as mods.settings, which has to stay managed, so the location can't be
    // filtered whole.
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => path.InFolder(SavesFolder));
}
