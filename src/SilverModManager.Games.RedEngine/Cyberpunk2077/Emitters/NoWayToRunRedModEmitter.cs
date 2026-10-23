using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using static SilverModManager.Games.RedEngine.Constants;
namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;

/// <summary>
/// Warns when the REDmod DLC is installed but nothing on this machine can run its Windows executable.
/// </summary>
public partial class NoWayToRunRedModEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IToolRunner _toolRunner;

    /// <summary/>
    public NoWayToRunRedModEmitter(IServiceProvider serviceProvider) => _toolRunner = serviceProvider.GetRequiredService<IToolRunner>();

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var install = loadout.InstallationInstance;
        var redModPath = install.Locations.ToAbsolutePath(RedModPath);

        // Without the REDmod DLC there is nothing to run; MissingRedModEmitter covers that case.
        if (!redModPath.FileExists)
            yield break;

        if (!_toolRunner.CanRun(install, out var reason))
            yield return Diagnostics.CreateNoWayToRunRedMod(reason);
    }
}
