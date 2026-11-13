using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity;

/// <summary>
/// The default synchronizer, less the files the game's loader writes while the game runs. Without the filter every
/// session shows up as changes to the loadout.
/// </summary>
public class UnityGameSynchronizer(IServiceProvider provider, AUnityModLoader loader) : ALoadoutSynchronizer(provider)
{
    protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(loader.IsRuntimeOutput);
}
