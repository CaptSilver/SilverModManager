using Mutagen.Bethesda.Plugins.Records;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine;

internal static class PluginParsing
{
    /// <summary>
    /// Parses the plugin a sync node stands for, from the bytes of <paramref name="preferred"/> when a stream
    /// source has them.
    /// </summary>
    /// <remarks>
    /// A game file's loadout hash is the build the loadout was created for. After the game store patches the
    /// file, those bytes are gone from the disk and were never backed up, but the file now on disk is readable
    /// by its own hash. Returns <c>null</c> when neither can be read.
    /// </remarks>
    public static async ValueTask<IMod?> ParseNode(this ICreationEngineGame game, SyncNode node, Hash preferred, RelativePath name)
    {
        var header = await game.ParsePlugin(preferred, name);
        if (header is not null || !node.HaveDisk || node.Disk.Hash == preferred) return header;

        return await game.ParsePlugin(node.Disk.Hash, name);
    }
}
