using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// The REFramework loader of an RE Engine game: whether it is in a loadout, which mods need it, and what running the game
/// with it takes. Nothing here knows a game; the release name comes from the game's definition.
/// </summary>
/// <remarks>
/// The Wine override for the proxy DLL is declared here, next to the code that installs that DLL; an RE Engine game
/// implements <see cref="IWineDllOverrideRequirements"/> by forwarding to its loader.
/// </remarks>
public sealed class ReFrameworkLoader(ReEngineGameDefinition definition) : IWineDllOverrideRequirements
{
    private static readonly GamePath Proxy = new(LocationId.Game, ReFrameworkFiles.Loader);
    private static readonly string ScriptsPrefix = $"{ReFrameworkFiles.Root}/";

    /// <summary>The file in REFramework's releases to download for this game.</summary>
    public string Release => definition.ReFrameworkRelease;

    /// <summary>True when the loadout has dinput8.dll next to the game executable, enabled.</summary>
    public bool IsInstalled(Loadout.ReadOnly loadout) => EnabledFiles(loadout).Any(item => (GamePath)item.TargetPath == Proxy);

    /// <summary>The group of the first enabled file below <c>reframework</c>, or none. Loose <c>natives</c> files do not count: the game reads those itself.</summary>
    public Optional<LoadoutItemGroup.ReadOnly> FirstScriptOrPluginGroup(Loadout.ReadOnly loadout)
    {
        foreach (var item in EnabledFiles(loadout))
        {
            var path = (GamePath)item.TargetPath;
            if (path.LocationId != LocationId.Game || !path.Path.ToString().StartsWith(ScriptsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.HasParent()) return loadoutItem.Parent;
        }

        return Optional<LoadoutItemGroup.ReadOnly>.None;
    }

    /// <summary>
    /// The proxy is dinput8.dll, which Wine would otherwise load its own copy of; it has to be the native one, with the
    /// built-in as the fallback (REFramework's README: <c>WINEDLLOVERRIDES="dinput8.dll=n,b"</c>). Empty while the loader
    /// is not installed, because the override would only hide Wine's DLL.
    /// </summary>
    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout)
        => IsInstalled(loadout)
            ? [new WineDllOverride("dinput8", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])]
            : [];

    public ILibraryItemInstaller[] Installers(IServiceProvider provider) => [new ReFrameworkPackInstaller(provider), new ReEngineModInstaller(provider)];

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> EnabledFiles(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => item.AsLoadoutItem().IsEnabled());
}
