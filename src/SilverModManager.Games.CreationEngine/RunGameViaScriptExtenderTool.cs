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
        var extenderInLoadout = ScriptExtender.IsInLoadout(loadout, _scriptExtenderPath);

        if (!extenderInLoadout) return base.GetGamePath(loadout);
        return ValueTask.FromResult(loadout.InstallationInstance.Locations.ToAbsolutePath(_scriptExtenderPath));
    }
}

/// <summary>
/// Where a script extender's loader is looked for.
/// </summary>
public static class ScriptExtender
{
    /// <summary>
    /// Whether the loadout puts the loader at <paramref name="loader"/>.
    /// </summary>
    /// <remarks>
    /// The loader only counts when the loadout puts it there. Checking the disk would pick up a loader left behind
    /// by an earlier deployment, or miss one that is about to be deployed. A deleted-file record at the same path
    /// wins over a mod's copy, so it rules the loader out.
    /// </remarks>
    public static bool IsInLoadout(Loadout.ReadOnly loadout, GamePath loader)
    {
        var itemsAtPath = LoadoutItemWithTargetPath.FindByTargetPath(loadout.Db, loader.ToGamePathParentTuple(loadout.Id))
            .Where(x => x.AsLoadoutItem().IsEnabled())
            .ToArray();
        return itemsAtPath.Any(x => x.IsLoadoutFile()) && !itemsAtPath.Any(x => x.IsDeletedFile());
    }
}

/// <summary>
/// The loader of the script extender <typeparamref name="TGame"/> launches through, registered next to the game
/// so the game can tell whether the loadout carries it.
/// </summary>
public sealed record ScriptExtenderLoader<TGame>(GamePath Path);
