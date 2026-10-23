using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Finds the content a game installed into its own folder. Takes the installation and disk state, never a
/// loadout, because resetting the game has no loadout. Recognition is per file: an item is kept while any of
/// its files is on disk.
/// </summary>
public interface IGameInstalledContentRecognizer
{
    /// <summary>
    /// Paths (files or folders) that are the game's own record of installed content. The synchronizer filters
    /// them out; only the recognizer reads them. A path matches when it equals an entry or is inside one.
    /// </summary>
    IReadOnlyList<GamePath> RecordPaths { get; }

    /// <summary>
    /// Reads the records and returns the content found. Only files present in <paramref name="diskState"/>
    /// are returned.
    /// </summary>
    ValueTask<GameInstalledRecognition> Recognize(
        GameInstallation installation,
        IReadOnlyDictionary<GamePath, SyncNodePart> diskState,
        CancellationToken token);
}
