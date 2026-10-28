using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher;

/// <summary>The synchronizer of every Witcher game. The older two need nothing beyond the defaults.</summary>
public class WitcherSynchronizer(IServiceProvider provider, IGame game) : ALoadoutSynchronizer(provider)
{
    protected IGame Game { get; } = game;
}
