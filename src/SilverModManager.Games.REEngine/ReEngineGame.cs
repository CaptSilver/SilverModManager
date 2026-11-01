using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// An RE Engine game modded with loose files and REFramework, described by a <see cref="ReEngineGameDefinition"/>.
/// Every such game is an instance of this class, so nothing may look one up by type.
/// </summary>
public sealed class ReEngineGame : DefinedGame<ReEngineGameDefinition>, IReEngineGame
{
    public ReFrameworkLoader Loader { get; }

    public ReEngineGame(IServiceProvider provider, ReEngineGameDefinition definition) : base(provider, definition)
    {
        Loader = new ReFrameworkLoader(definition);
    }

    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout) => Loader.RequiredWineDllOverrides(loadout);

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new ReEngineGameSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) => Loader.Installers(provider);

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        .. base.CreateEmitters(provider),
        provider.GetRequiredService<ReFrameworkMissingEmitter>(),
        provider.GetRequiredService<WineDllOverrideEmitter>(),
    ];
}
