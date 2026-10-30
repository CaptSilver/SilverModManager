using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

public interface IIntrinsicFile
{
    /// <summary>
    /// The game path of this file.
    /// </summary>
    public GamePath Path { get; }

    /// <summary>
    /// Write the contents of this file to the stream.
    /// </summary>
    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree);

    /// <summary>
    /// Ingest the contents of the stream into the loadout
    /// </summary>
    public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx);
}

/// <summary>
/// An intrinsic file whose <see cref="IIntrinsicFile.Write"/> reads the loadout and the sync tree and nothing else: no
/// file store, no game folder, no bytes on disk. The Apply button's status renders such a file on every change to the
/// loadout to see whether it would come out different from what the last apply wrote. A file that reads anything
/// else must not declare this, because the status would render it without the state an apply has prepared.
/// </summary>
public interface IIntrinsicFileRenderedFromLoadout : IIntrinsicFile;
