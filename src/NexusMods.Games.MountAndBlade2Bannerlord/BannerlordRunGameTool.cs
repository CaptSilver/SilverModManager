using Bannerlord.LauncherManager.Utils;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Generic;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using static Bannerlord.LauncherManager.Constants;
namespace NexusMods.Games.MountAndBlade2Bannerlord;

/// <summary>
/// This is to run the game or SMAPI using the shell, which allows them to start their own console,
/// allowing users to interact with it.
/// </summary>
public class BannerlordRunGameTool : RunGameTool<Bannerlord>
{
    private readonly ILogger<BannerlordRunGameTool> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameToolRunner _runner;

    public BannerlordRunGameTool(IServiceProvider serviceProvider, Bannerlord game, GameToolRunner runner)
        : base(serviceProvider, game)
    {
        _serviceProvider = serviceProvider;
        _runner = runner;
        _logger = serviceProvider.GetRequiredService<ILogger<BannerlordRunGameTool>>();
    }

    protected override bool UseShell { get; set; } = false;
    
    public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        // We need to 'inject' the current set of enabled modules, the same way the launcher would.
        var args = await GetBannerlordExeCommandlineArgs(loadout, cancellationToken);
        var exe = loadout.InstallationInstance.Locations[LocationId.Game].Path;

        if (loadout.LocateBLSE(out var blseRelativePath))
        {
            exe = exe/blseRelativePath;

            // Note(sewer): On Linux there is no guarantee .NET Framework is
            // installed; instead, Framework stuff may be run on
            // Mono. Because BLSE interacts with low level components of the runtime,
            // we will instead use a 'hidden' BLSE feature to use the .NET Runtime
            // stored in the game folder. In this case, that's .NET 6 at the time of
            // writing.
            args = [..args, "/forcenetcore"];
        }
        else
        {
            exe = exe/BinFolder/Win64Configuration/BannerlordExecutable;
        }

        return new LaunchSpec(exe, args);
    }

    public override async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        var spec = await GetLaunchSpec(loadout, cancellationToken);

        // External arguments specified by outside entities are preserved, ahead of the ones the tool adds.
        var command = Cli.Wrap(spec.Exe.ToString())
            .WithArguments([..commandLineArgs ?? [], ..spec.ExtraArgs])
            .WithWorkingDirectory(spec.Exe.Parent.ToString());

        // Note(sewer): We use the tool runner to execute an alternative process,
        //              but because the UI expects `RunGameTool`, we override the `RunGameTool's` executor
        //              with `GameToolRunner` implementation.
        await _runner.ExecuteAsync(loadout, command, true, cancellationToken);
    }

    // TODO: Refactor Bannerlord.LauncherManager so we don't have to copy (some) of the commandline logic.
    //       (Since LauncherManager has dependencies on code we don't need/want to provide).
    private async Task<string[]> GetBannerlordExeCommandlineArgs(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        // Set the (automatic) load order.
        // Copied from Bannerlord.LauncherManager
        var manifestPipeline = Pipelines.GetManifestPipeline(_serviceProvider);
        var modules = (await Helpers.GetAllManifestsAsync(_logger, loadout, manifestPipeline, cancellationToken).ToArrayAsync(cancellationToken))
            .Select(x => x.Item2);
        var sortedModules = SortHelper.AutoSort(Hack.GetDummyBaseGameModules()
            .Concat(modules)).Select(x => x.Id).ToArray();
        var loadOrderCli = sortedModules.Length > 0 ? $"_MODULES_*{string.Join("*", sortedModules)}*_MODULES_" : string.Empty;

        // Add the new arguments
        return ["/singleplayer", loadOrderCli];
    }
}
