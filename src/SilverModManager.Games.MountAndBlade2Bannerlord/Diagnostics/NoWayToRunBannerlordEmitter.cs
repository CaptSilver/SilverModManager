using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Diagnostics;

/// <summary>
/// Warns when nothing on this machine can run Bannerlord's Windows executables.
/// </summary>
public partial class NoWayToRunBannerlordEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IToolRunner _toolRunner;

    /// <summary/>
    public NoWayToRunBannerlordEmitter(IServiceProvider serviceProvider) => _toolRunner = serviceProvider.GetRequiredService<IToolRunner>();

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        if (!_toolRunner.CanRun(loadout.InstallationInstance, out var reason))
            yield return Diagnostics.CreateNoWayToRunBannerlord(reason);
    }
}
