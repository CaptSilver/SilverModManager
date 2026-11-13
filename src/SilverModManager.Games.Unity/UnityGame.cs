using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

/// <summary>
/// A Unity game whose mods come from Thunderstore and run on the loader its row names, described by a <see cref="UnityGameDefinition"/>.
/// Every Unity game is an instance of this class, so nothing may look one up by type.
/// </summary>
public sealed class UnityGame : DefinedGame<UnityGameDefinition>, IUnityGame, IGameNativeCode, IGameUpdateSensitivity, IWinePrefixRequirements
{
    public AUnityModLoader Loader { get; }

    public UnityGame(IServiceProvider provider, UnityGameDefinition definition) : base(provider, definition)
    {
        Loader = UnityModLoaders.Create(definition);
    }

    public string ThunderstoreCommunity => Definition.ThunderstoreCommunity;
    public string ThunderstoreLoaderPackage => Definition.ThunderstoreLoaderPackage;
    public IReadOnlyList<string> ThunderstoreLoaderAliases => Definition.ThunderstoreLoaderAliases;

    public bool HooksGameCode(GamePath path) => Loader.HooksGameCode(path);

    // The proxy DLL sits beside the executable for every Unity game, whichever loader it belongs to.
    public NativeCodeRole? RoleOf(GamePath path)
    {
        if (Loader.HooksGameCode(path)) return NativeCodeRole.LoaderPlugin;
        if (path == Loader.Proxy || Loader.IsLoaderRuntime(path)) return NativeCodeRole.LoaderRuntime;
        return null;
    }

    public TimeSpan? DateWindow => Definition.DateWindow;

    public string WhatBreaks => Loader.WhatBreaks;

    public NamedLink? ExtenderPage => null;

    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout) => Loader.RequiredWineDllOverrides(loadout);

    public IReadOnlyList<PrefixComponentId> RequiredPrefixComponents(Loadout.ReadOnly loadout) => Loader.RequiredPrefixComponents(loadout);

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new UnityGameSynchronizer(provider, Loader);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) => Loader.Installers(provider);

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        .. base.CreateEmitters(provider),
        provider.GetRequiredService<UnityLoaderMissingEmitter>(),
        provider.GetRequiredService<UnityRuntimeBranchEmitter>(),
        provider.GetRequiredService<WineDllOverrideEmitter>(),
        provider.GetRequiredService<WinePrefixComponentsEmitter>(),
    ];
}
