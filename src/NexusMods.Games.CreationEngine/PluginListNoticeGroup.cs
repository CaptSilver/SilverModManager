using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Games.CreationEngine;

/// <summary>
/// The plugin list files of one game that share a single replaced-edits notice (plugins.txt and loadorder.txt).
/// Each file the game changed compares every file of the group the game changed and stages the combined result, so
/// the notice is the same whichever of them the synchronizer ingests first; the first to stage is the only one to
/// write it.
/// </summary>
public sealed class PluginListNoticeGroup
{
    private readonly List<PluginsFile> _files = [];
    private ITransaction? _stagedIn;

    internal IReadOnlyList<PluginsFile> Files => _files;

    internal void Add(PluginsFile file) => _files.Add(file);

    /// <summary>True the first time it is asked for a transaction, false for any later file in the same one.</summary>
    internal bool TryClaim(ITransaction tx)
    {
        if (ReferenceEquals(_stagedIn, tx)) return false;
        _stagedIn = tx;
        return true;
    }
}
