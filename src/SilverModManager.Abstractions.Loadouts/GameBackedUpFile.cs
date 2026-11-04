using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// This is a GC root that prevents garbage collection of a file.
/// 
/// This represents a file which belongs to the game, i.e. game files.
/// 
/// This file is backed up by the Synchronizer, when it decides to take a backup
/// of game files which are on disk and not archived. (Needed for rollback/undo)
///
/// Mechanics are roughly as follows:
///
/// -> File in game hashes DB => GameBackedUpFile   | (rooted by GameBackedUpFile via file hashes DB match)
/// -> File in overrides => !GameBackedUpFile       | (rooted by overrides mod)
/// </summary>
/// <remarks>
///     There is a 'catch', for when we don't have game files in the hashes DB, 
///     they may end up in overrides, which always win. Problematic if a mod requires
///     to override base game files. But as discussed in a meeting, that is a problem
///     for another day.
/// </remarks>
[PublicAPI]
public partial class GameBackedUpFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.GC.BackedUpFile";

    /// <summary>
    /// Hash of the file.
    /// </summary>
    public static readonly HashAttribute Hash = new(Namespace, nameof(Hash));
    
    /// <summary>
    /// The installation to which this file belongs.
    /// This is stored in the case of a future migration towards a system where
    /// we properly clean up files aft
    /// </summary>
    public static readonly ReferenceAttribute<Sdk.Games.GameInstallMetadata> GameInstall = new(Namespace, nameof(GameInstall));

    public partial struct ReadOnly
    {
        /// <summary>The backed-up file's store key as an identity.</summary>
        public FileIdentity Identity => FileIdentity.XxHash3(GameBackedUpFile.Hash.Get(this));
    }

    /// <summary>
    /// Roots the stored copies of <paramref name="files"/> for <paramref name="install"/> in a transaction of its own,
    /// skipping any the install already roots. It commits before it returns, so a caller that goes on to delete the
    /// files from disk can rely on the roots surviving a failure later in the same apply.
    /// </summary>
    /// <param name="files">Store keys (xxHash3 identities).</param>
    public static async Task Root(IConnection connection, Sdk.Games.GameInstallMetadataId install, IEnumerable<FileIdentity> files)
    {
        using var tx = connection.BeginTransaction();
        var alreadyRooted = All(connection.Db).Where(f => f.GameInstallId == install).Select(f => f.Hash).ToHashSet();
        foreach (var file in files)
        {
            var hash = file.XxHash3();
            if (!alreadyRooted.Add(hash)) continue;
            _ = new New(tx)
            {
                GameInstallId = install,
                Hash = hash,
            };
        }
        await tx.Commit();
    }
}
