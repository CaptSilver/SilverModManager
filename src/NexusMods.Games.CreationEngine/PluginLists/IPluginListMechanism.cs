using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.PluginLists;

/// <summary>
/// How a game keeps its load order: which files the app writes at sync time and how the game's own copy is read back.
/// </summary>
public interface IPluginListMechanism
{
    /// <summary>The files the app generates for this mechanism at sync time.</summary>
    IEnumerable<IIntrinsicFile> IntrinsicFiles(ICreationEngineGame game, Loadout.ReadOnly loadout, IServiceProvider provider);

    /// <summary>
    /// The plugin list as the game has it on disk right now, in file order.
    /// </summary>
    /// <returns><c>null</c> when the game has not written one yet.</returns>
    Task<GamePluginListState?> ReadGameState(ICreationEngineGame game, Loadout.ReadOnly loadout);

    /// <summary>Whether the game derives load order from file timestamps instead of from the list.</summary>
    bool OrdersByTimestamp { get; }
}

/// <summary>A plugin list file as read from disk. A plugin the file does not mention is not in <paramref name="Entries"/>.</summary>
public sealed record GamePluginListState(IReadOnlyList<(string Name, bool Active)> Entries);
