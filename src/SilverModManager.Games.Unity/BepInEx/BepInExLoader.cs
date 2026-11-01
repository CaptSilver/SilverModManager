using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.Generic.Loaders;

namespace SilverModManager.Games.Unity.BepInEx;

/// <summary>
/// The BepInEx loader of a Unity game: which packages are its loader pack, whether the pack is in a loadout, and what
/// running the game with it needs. Package names come from the game's definition; nothing here knows a game.
/// </summary>
/// <remarks>
/// The doorstop proxy, its Wine override and the plugins folder come from <see cref="AProxyDllLoader"/>; a Unity game
/// implements <see cref="SilverModManager.Abstractions.Games.IWineDllOverrideRequirements"/> by forwarding to its loader.
/// </remarks>
public sealed class BepInExLoader(UnityGameDefinition definition)
    : AProxyDllLoader(BepInExFiles.DoorstopProxy, $"{BepInExFiles.Root}/{BepInExFiles.Plugins}")
{
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

    /// <summary>True when an enabled file of the loadout sits below <c>BepInEx/plugins</c>.</summary>
    public bool HasPlugins(Loadout.ReadOnly loadout) => FirstModGroup(loadout).HasValue;

    public ILibraryItemInstaller[] Installers(IServiceProvider provider) => [new BepInExPackInstaller(provider, this), new BepInExPluginInstaller(provider, this)];
}
