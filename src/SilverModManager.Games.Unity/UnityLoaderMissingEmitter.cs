using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Loaders;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

/// <summary>Mods in the loadout and no loader pack to load them; the report names the game's loader and sends the player to Thunderstore for the pack.</summary>
public class UnityLoaderMissingEmitter : AProxyLoaderMissingEmitter<IUnityGame>
{
    protected override AProxyDllLoader LoaderOf(IUnityGame game) => game.Loader;

    protected override Diagnostic Report(IUnityGame game, LoadoutItemGroup.ReadOnly group, Loadout.ReadOnly loadout)
        => Diagnostics.CreateLoaderMissing(Group: group.ToReference(loadout), Loader: game.Loader.DisplayName, Package: game.Loader.LoaderPackage);
}
