using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Loaders;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.REEngine;

/// <summary>Scripts or plugins in the loadout and no REFramework to load them.</summary>
public class ReFrameworkMissingEmitter : AProxyLoaderMissingEmitter<IReEngineGame>
{
    protected override AProxyDllLoader LoaderOf(IReEngineGame game) => game.Loader;

    protected override Diagnostic Report(IReEngineGame game, LoadoutItemGroup.ReadOnly group, Loadout.ReadOnly loadout)
        => Diagnostics.CreateReFrameworkMissing(Group: group.ToReference(loadout), Release: game.Loader.Release);
}
