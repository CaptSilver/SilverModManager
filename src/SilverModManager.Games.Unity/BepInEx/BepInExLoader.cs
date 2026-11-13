using SilverModManager.Abstractions.Library.Installers;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

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
    : AUnityModLoader(definition, BepInExFiles.DoorstopProxy, $"{BepInExFiles.Root}/{BepInExFiles.Plugins}")
{
    private static readonly GamePath PluginsFolder = new(LocationId.Game, $"{BepInExFiles.Root}/{BepInExFiles.Plugins}");
    private static readonly GamePath CoreFolder = new(LocationId.Game, $"{BepInExFiles.Root}/{BepInExFiles.Core}");
    private static readonly Extension Dll = new(".dll");

    public override string DisplayName => "BepInEx";

    public override string WhatBreaks => "BepInEx plugins are built against one version of the game and may stop loading after an update; they may need a new release from their author.";

    public override ILibraryItemInstaller[] Installers(IServiceProvider provider) => [new BepInExPackInstaller(provider, this), new BepInExPluginInstaller(provider, this)];

    /// <remarks>
    /// These are the paths BepInEx is expected to write at runtime: its log and the type cache under <c>BepInEx/cache</c>.
    /// Not checked against the BepInEx source; a played session confirms them.
    /// </remarks>
    public override bool IsRuntimeOutput(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;

        var relative = path.Path.ToString();
        return relative.Equals("BepInEx/LogOutput.log", StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("BepInEx/cache/", StringComparison.OrdinalIgnoreCase);
    }

    // The loader pack's own files (winhttp.dll, BepInEx/core) are not listed: they are not a mod the user picked.
    public override bool HooksGameCode(GamePath path) => NativeCodePaths.IsLibraryBelow(path, PluginsFolder);

    public override bool IsLoaderRuntime(GamePath path) => NativeCodePaths.IsDirectlyIn(path, CoreFolder, Dll);
}
