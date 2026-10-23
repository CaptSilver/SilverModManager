using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine;

public class RunGameViaScriptExtenderTool<TGame> : RunGameTool<TGame> where TGame : IGame
{
    private readonly GamePath _scriptExtenderPath;
    
    private RunGameViaScriptExtenderTool(IServiceProvider serviceProvider, TGame game, GamePath scriptExtenderPath) : base(serviceProvider, game)
    {
        _scriptExtenderPath = scriptExtenderPath;
    }

    public static IRunGameTool Create(IServiceProvider serviceProvider, GamePath scriptExtenderPath)
    {
        return new RunGameViaScriptExtenderTool<TGame>(serviceProvider, serviceProvider.GetRequiredService<TGame>(), scriptExtenderPath);
    }

    protected override ValueTask<AbsolutePath> GetGamePath(Loadout.ReadOnly loadout)
    {
        // The loader only counts when the loadout puts it there. Checking the disk would pick up a
        // loader left behind by an earlier deployment, or miss one that is about to be deployed.
        // A deleted-file record at the same path wins over a mod's copy, so it rules the loader out.
        var itemsAtPath = LoadoutItemWithTargetPath.FindByTargetPath(loadout.Db, _scriptExtenderPath.ToGamePathParentTuple(loadout.Id))
            .Where(x => x.AsLoadoutItem().IsEnabled())
            .ToArray();
        var extenderInLoadout = itemsAtPath.Any(x => x.IsLoadoutFile()) && !itemsAtPath.Any(x => x.IsDeletedFile());

        if (!extenderInLoadout) return base.GetGamePath(loadout);
        return ValueTask.FromResult(loadout.InstallationInstance.Locations.ToAbsolutePath(_scriptExtenderPath));
    }
}
