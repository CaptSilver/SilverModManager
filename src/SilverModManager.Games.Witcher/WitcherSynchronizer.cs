using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher;

/// <summary>The base of the synchronizers of the Witcher games that have logic of their own.</summary>
public class WitcherSynchronizer(IServiceProvider provider, IGame game) : ALoadoutSynchronizer(provider)
{
    protected IGame Game { get; } = game;
}
