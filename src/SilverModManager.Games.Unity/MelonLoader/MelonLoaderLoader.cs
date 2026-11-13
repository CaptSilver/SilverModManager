using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity.MelonLoader;

/// <summary>
/// The MelonLoader loader of a Unity game. The loader pack puts <c>version.dll</c> beside the executable, and mods are
/// read from <c>Mods</c>. An IL2CPP game also needs the .NET 6 desktop runtime in its prefix, because MelonLoader runs
/// its assembly generator on first launch.
/// </summary>
public sealed class MelonLoaderLoader(UnityGameDefinition definition)
    : AUnityModLoader(definition, MelonLoaderFiles.Proxy, MelonLoaderFiles.Mods)
{
    private static readonly GamePath ModsFolder = new(LocationId.Game, MelonLoaderFiles.Mods);
    private static readonly GamePath PluginsFolder = new(LocationId.Game, MelonLoaderFiles.Plugins);
    private static readonly GamePath RootFolder = new(LocationId.Game, MelonLoaderFiles.Root);

    // What MelonLoader regenerates or appends to on every launch.
    private static readonly string[] RuntimeFolders =
    [
        $"{MelonLoaderFiles.Root}/Logs/",
        $"{MelonLoaderFiles.Root}/Il2CppAssemblies/",
        $"{MelonLoaderFiles.Root}/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/",
    ];

    private static readonly string[] RuntimeFiles =
    [
        $"{MelonLoaderFiles.Root}/Latest.log",
        $"{MelonLoaderFiles.UserData}/Loader.cfg",
        $"{MelonLoaderFiles.UserData}/MelonPreferences.cfg",
    ];

    public override string DisplayName => "MelonLoader";

    public override string WhatBreaks => "MelonLoader mods are built against one version of the game and may stop loading after an update; they may need a new release from their author.";

    public override ILibraryItemInstaller[] Installers(IServiceProvider provider) => [new MelonLoaderPackInstaller(provider, this), new MelonLoaderModInstaller(provider, this)];

    /// <remarks>
    /// Not <c>MelonLoader/Dependencies</c> as a whole: it also holds the support modules the loader pack ships, which are
    /// part of the loadout.
    /// </remarks>
    public override bool IsRuntimeOutput(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;

        var relative = path.Path.ToString();
        return RuntimeFiles.Any(file => relative.Equals(file, StringComparison.OrdinalIgnoreCase))
               || RuntimeFolders.Any(folder => relative.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    public override bool HooksGameCode(GamePath path)
        => NativeCodePaths.IsLibraryBelow(path, ModsFolder) || NativeCodePaths.IsLibraryBelow(path, PluginsFolder);

    public override bool IsLoaderRuntime(GamePath path) => NativeCodePaths.IsLibraryBelow(path, RootFolder);

    public override IReadOnlyList<PrefixComponentId> RequiredPrefixComponents(Loadout.ReadOnly loadout)
        => UnityRuntimeBranch.Of(loadout.InstallationInstance, Definition) == UnityRuntime.Il2Cpp ? [PrefixComponents.DotNetDesktop6.Id] : [];
}
