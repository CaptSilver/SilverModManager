using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Loaders;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity.BepInEx;

/// <summary>Plugins in the loadout and no loader pack to load them; the report sends the player to Thunderstore for the pack.</summary>
public class BepInExMissingEmitter : AProxyLoaderMissingEmitter<IUnityGame>
{
    protected override AProxyDllLoader LoaderOf(IUnityGame game) => game.Loader;

    protected override Diagnostic Report(IUnityGame game, LoadoutItemGroup.ReadOnly group, Loadout.ReadOnly loadout)
        => Diagnostics.CreateBepInExMissing(Group: group.ToReference(loadout), Package: game.Loader.LoaderPackage);
}
