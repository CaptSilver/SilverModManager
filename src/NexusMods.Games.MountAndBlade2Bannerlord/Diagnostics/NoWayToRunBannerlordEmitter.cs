using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.MountAndBlade2Bannerlord.Diagnostics;

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
