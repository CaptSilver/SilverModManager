using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.PluginLists;

/// <summary>
/// A game that reads its plugins from a text file with one plugin per line, in load order. The two flavours differ
/// only in how they mark an active plugin.
/// </summary>
public abstract class APluginsTxtMechanism : IPluginListMechanism
{
    /// <summary>What goes in front of the name of an active plugin; empty when the file lists active plugins only.</summary>
    protected abstract string ActiveMarker { get; }

    public bool OrdersByTimestamp => false;

    public IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        yield return new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), game, provider.GetRequiredService<ISorter>(), ActiveMarker);
    }

    public async Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout)
    {
        var file = loadout.InstallationInstance.Locations.ToAbsolutePath(game.PluginsFile);
        if (!file.FileExists) return null;

        var entries = new List<(string Name, bool Active)>();
        foreach (var raw in (await file.ReadAllTextAsync()).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (ActiveMarker.Length == 0)
                entries.Add((line, true));
            else if (line.StartsWith(ActiveMarker, StringComparison.Ordinal))
                entries.Add((line[ActiveMarker.Length..], true));
            else
                entries.Add((line, false));
        }
        return new GamePluginListState(entries);
    }
}

/// <summary>Active plugins are prefixed with <c>*</c> and unmarked ones are inactive (Skyrim SE, Fallout 4 and newer).</summary>
public sealed class AsteriskMechanism : APluginsTxtMechanism
{
    protected override string ActiveMarker => "*";
}

/// <summary>One plugin name per line; everything listed is active (Skyrim LE, Enderal).</summary>
public sealed class PlainMechanism : APluginsTxtMechanism
{
    protected override string ActiveMarker => "";
}
