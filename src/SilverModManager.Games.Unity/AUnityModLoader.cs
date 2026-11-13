using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Loaders;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

/// <summary>
/// The mod loader of a Unity game: which packages are its loader pack, whether the pack is in a loadout, how its mods are
/// installed and what running the game with it needs. Package names come from the game's definition; a loader
/// class knows one loader, never a game.
/// </summary>
public abstract class AUnityModLoader(UnityGameDefinition definition, string proxyFile, string modsFolder) : AProxyDllLoader(proxyFile, modsFolder)
{
    public UnityGameDefinition Definition => definition;

    public string LoaderPackage => definition.ThunderstoreLoaderPackage;

    public IReadOnlyList<string> LoaderAliases => definition.ThunderstoreLoaderAliases;

    /// <summary>
    /// Whether a library item is the loader pack: its name, less the extension and the version, is the loader package
    /// or one of its aliases. Thunderstore downloads are named <c>Namespace-Name-Version.zip</c>.
    /// </summary>
    public bool IsLoaderPackName(string libraryItemName)
    {
        var package = ThunderstorePackageName.FromArchiveName(libraryItemName) ?? Path.GetFileNameWithoutExtension(libraryItemName);

        return package.Equals(LoaderPackage, StringComparison.OrdinalIgnoreCase)
               || LoaderAliases.Any(alias => package.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when an enabled file of the loadout sits below the loader's mods folder.</summary>
    public bool HasMods(Loadout.ReadOnly loadout) => FirstModGroup(loadout).HasValue;

    /// <summary>The enabled libraries of mods in the loadout, each with the group it was installed as.</summary>
    public IEnumerable<(GamePath Path, LoadoutItemGroup.ReadOnly Group)> EnabledModLibraries(Loadout.ReadOnly loadout)
    {
        foreach (var item in EnabledFiles(loadout))
        {
            var path = (GamePath)item.TargetPath;
            var loadoutItem = item.AsLoadoutItem();
            if (HooksGameCode(path) && loadoutItem.HasParent()) yield return (path, loadoutItem.Parent);
        }
    }

    /// <summary>The loader's name as the player knows it, for reports.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Why a game update can break the loader's mods.</summary>
    public abstract string WhatBreaks { get; }

    public abstract ILibraryItemInstaller[] Installers(IServiceProvider provider);

    /// <summary>True for a file the loader writes while the game runs: logs, caches, generated assemblies. Such a file is not a change to the loadout.</summary>
    public abstract bool IsRuntimeOutput(GamePath path);

    /// <summary>True for a library of a mod, which loads into the game's process and was built against one build of the game.</summary>
    public abstract bool HooksGameCode(GamePath path);

    /// <summary>True for a library of the loader itself, other than its proxy DLL.</summary>
    public abstract bool IsLoaderRuntime(GamePath path);

    /// <summary>The Wine prefix components the loader needs for this loadout. None by default.</summary>
    public virtual IReadOnlyList<PrefixComponentId> RequiredPrefixComponents(Loadout.ReadOnly loadout) => [];
}
