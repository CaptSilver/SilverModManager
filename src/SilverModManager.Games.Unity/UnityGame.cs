using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

/// <summary>
/// A Unity game whose mods are BepInEx plugins from Thunderstore, described by a <see cref="UnityGameDefinition"/>.
/// Every Unity game is an instance of this class, so nothing may look one up by type.
/// </summary>
public sealed class UnityGame : DefinedGame<UnityGameDefinition>, IUnityGame, IGameNativeCode, IGameUpdateSensitivity
{
    private static readonly GamePath PluginsFolder = new(LocationId.Game, $"{BepInExFiles.Root}/{BepInExFiles.Plugins}");

    public BepInExLoader Loader { get; }

    public UnityGame(IServiceProvider provider, UnityGameDefinition definition) : base(provider, definition)
    {
        Loader = new BepInExLoader(definition);
    }

    public string ThunderstoreCommunity => Definition.ThunderstoreCommunity;
    public string ThunderstoreLoaderPackage => Definition.ThunderstoreLoaderPackage;
    public IReadOnlyList<string> ThunderstoreLoaderAliases => Definition.ThunderstoreLoaderAliases;

    // The loader pack's own files (winhttp.dll, BepInEx/core) are not listed: they are not a mod the user picked.
    public bool HooksGameCode(GamePath path) => NativeCodePaths.IsLibraryBelow(path, PluginsFolder);

    public TimeSpan? DateWindow => Definition.DateWindow;

    public string WhatBreaks => "BepInEx plugins are built against one version of the game and may stop loading after an update; they may need a new release from their author.";

    public NamedLink? ExtenderPage => null;

    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout) => Loader.RequiredWineDllOverrides(loadout);

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new UnityGameSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) => Loader.Installers(provider);

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        .. base.CreateEmitters(provider),
        provider.GetRequiredService<BepInExMissingEmitter>(),
        provider.GetRequiredService<WineDllOverrideEmitter>(),
    ];
}
