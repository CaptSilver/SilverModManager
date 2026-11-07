using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine;

internal static class PluginNodeParsing
{
    /// <summary>
    /// Parses the plugin a sync node stands for, from the bytes of <paramref name="preferred"/> when a stream
    /// source has them.
    /// </summary>
    /// <remarks>
    /// A game file's loadout hash is the build the loadout was created for. After the game store patches the
    /// file, those bytes are gone from the disk and were never backed up, but the file now on disk is readable
    /// by its own hash. A loadout file that is also on disk but in no store (a game master the loadout holds no
    /// bytes of) is read from its place in the game folder. Returns <c>null</c> when none of them can be read.
    /// </remarks>
    public static async ValueTask<PluginHeader?> ParseNode(this ICreationEngineGame game, SyncNode node, FileIdentity preferred, GamePath path, GameInstallation installation)
    {
        var name = path.Path.FileName;
        var header = await game.ParsePlugin(preferred, name);
        if (header is not null || !node.HaveDisk) return header;

        if (node.Disk.Identity.MatchSameKind(preferred) != IdentityMatch.Same)
        {
            header = await game.ParsePlugin(node.Disk.Identity, name);
            if (header is not null) return header;
        }

        return node.HaveLoadout ? await game.ParsePluginFile(installation.Locations.ToAbsolutePath(path), name) : null;
    }
}
