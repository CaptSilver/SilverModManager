using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Loaders;

/// <summary>
/// A mod loader that gets into the game by being a DLL the game loads by name (winhttp.dll, dinput8.dll) placed next to
/// the executable, and that then reads mods from one folder below the game folder. A game's loader says which two
/// names those are; the rest (is it installed, which group holds the first mod, what Wine needs) is the same for all.
/// </summary>
/// <remarks>
/// A game implements <see cref="IWineDllOverrideRequirements"/> by forwarding to its loader.
/// </remarks>
public abstract class AProxyDllLoader(string proxyFile, string modsFolder) : IWineDllOverrideRequirements
{
    private readonly string _modsPrefix = $"{modsFolder}/";

    /// <summary>The proxy DLL, next to the game executable.</summary>
    public GamePath Proxy { get; } = new(LocationId.Game, proxyFile);

    /// <summary>The proxy's name as Wine overrides know it: the file name without <c>.dll</c>.</summary>
    public string WineDllName { get; } = Path.GetFileNameWithoutExtension(proxyFile);

    /// <summary>True when the loadout has the proxy next to the game executable, enabled.</summary>
    public bool IsInstalled(Loadout.ReadOnly loadout) => EnabledFiles(loadout).Any(item => (GamePath)item.TargetPath == Proxy);

    /// <summary>The group of the first enabled file below the mods folder, or none.</summary>
    public Optional<LoadoutItemGroup.ReadOnly> FirstModGroup(Loadout.ReadOnly loadout)
    {
        foreach (var item in EnabledFiles(loadout))
        {
            var path = (GamePath)item.TargetPath;
            if (path.LocationId != LocationId.Game || path.Path.ToString().StartsWith(_modsPrefix, StringComparison.OrdinalIgnoreCase) is false) continue;
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.HasParent()) return loadoutItem.Parent;
        }

        return Optional<LoadoutItemGroup.ReadOnly>.None;
    }

    /// <summary>
    /// The proxy is a DLL Wine would otherwise load its own copy of; it has to be the native one, with the built-in as
    /// the fallback. Empty while the loader is not installed, because the override would only hide Wine's DLL.
    /// </summary>
    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout)
        => IsInstalled(loadout)
            ? [new WineDllOverride(WineDllName, [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])]
            : [];

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> EnabledFiles(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => item.AsLoadoutItem().IsEnabled());
}
