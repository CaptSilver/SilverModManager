using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity.BepInEx;

/// <summary>
/// The BepInEx loader of a Unity game: which packages are its loader pack, whether the pack is in a loadout, and what
/// running the game with it needs. Package names come from the game's definition; nothing here knows a game.
/// </summary>
/// <remarks>
/// The Wine override for the proxy DLL is declared here, next to the code that installs that DLL; a Unity game
/// implements <see cref="IWineDllOverrideRequirements"/> by forwarding to its loader.
/// </remarks>
public sealed class BepInExLoader(UnityGameDefinition definition) : IWineDllOverrideRequirements
{
    private static readonly GamePath DoorstopProxy = new(LocationId.Game, BepInExFiles.DoorstopProxy);
    private static readonly string PluginsPrefix = $"{BepInExFiles.Root}/{BepInExFiles.Plugins}/";

    public string LoaderPackage => definition.ThunderstoreLoaderPackage;
    public IReadOnlyList<string> LoaderAliases => definition.ThunderstoreLoaderAliases;

    /// <summary>
    /// Whether a library item is the loader pack: its name, less the extension and the version, is the loader package
    /// or one of its aliases. Thunderstore downloads are named <c>Namespace-Name-Version.zip</c>.
    /// </summary>
    public bool IsLoaderPackName(string libraryItemName)
    {
        string package;
        try
        {
            package = BepInExPackageFolder.For(libraryItemName);
        }
        catch (BepInExLayoutException)
        {
            // A name that makes no folder is no package's name; the installers decline such an archive themselves.
            return false;
        }

        return package.Equals(LoaderPackage, StringComparison.OrdinalIgnoreCase)
               || LoaderAliases.Any(alias => package.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the loadout has the doorstop proxy next to the game executable, enabled.</summary>
    public bool IsInstalled(Loadout.ReadOnly loadout) => EnabledTargets(loadout).Contains(DoorstopProxy);

    /// <summary>True when an enabled file of the loadout sits below <c>BepInEx/plugins</c>.</summary>
    public bool HasPlugins(Loadout.ReadOnly loadout) => FirstPluginGroup(loadout).HasValue;

    /// <summary>The group of the first enabled file below <c>BepInEx/plugins</c>, or none.</summary>
    public Optional<LoadoutItemGroup.ReadOnly> FirstPluginGroup(Loadout.ReadOnly loadout)
    {
        foreach (var item in EnabledFiles(loadout))
        {
            var path = (GamePath)item.TargetPath;
            if (path.LocationId != LocationId.Game || !path.Path.ToString().StartsWith(PluginsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.HasParent()) return loadoutItem.Parent;
        }

        return Optional<LoadoutItemGroup.ReadOnly>.None;
    }

    /// <summary>
    /// The proxy is winhttp.dll, which Wine would otherwise load its own copy of; it has to be the native one, with the
    /// built-in as the fallback. Empty while the pack is not installed, because the override would only hide Wine's DLL.
    /// </summary>
    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout)
        => IsInstalled(loadout)
            ? [new WineDllOverride("winhttp", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])]
            : [];

    public ILibraryItemInstaller[] Installers(IServiceProvider provider) => [new BepInExPackInstaller(provider, this), new BepInExPluginInstaller(provider, this)];

    private static IEnumerable<GamePath> EnabledTargets(Loadout.ReadOnly loadout)
        => EnabledFiles(loadout).Select(item => (GamePath)item.TargetPath);

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> EnabledFiles(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => item.AsLoadoutItem().IsEnabled());
}
