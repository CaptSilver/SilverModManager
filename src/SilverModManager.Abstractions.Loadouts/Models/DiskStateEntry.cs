using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts;

public partial class DiskStateEntry : IModelDefinition
{
    /// <summary>
    /// Put entries in a user partition so they are all grouped together
    /// </summary>
    public static readonly PartitionId EntryPartition = PartitionId.User(4);
    
    private const string Namespace = "SilverModManager.Loadouts.DiskStateEntry";
    
    /// <summary>
    /// The path to the file
    /// </summary>
    public static readonly GamePathParentAttribute Path = new(Namespace, nameof(Path));

    /// <summary>
    /// The identity of the file: a hash kind and its digest. Always written.
    /// </summary>
    public static readonly FileIdentityAttribute Identity = new(Namespace, nameof(Identity));

    /// <summary>
    /// The xxHash3 of the file. The attribute id stays "Hash" so existing rows, the SQL column and
    /// <c>WHERE Hash = …</c> lookups keep working; the C# name is XxHash3 so every reader has to say it wants an xxHash3.
    /// Present exactly when <see cref="Identity"/> is an xxHash3, and equal to it.
    /// </summary>
    public static readonly HashAttribute XxHash3 = new(Namespace, "Hash") { IsOptional = true };
    
    /// <summary>
    /// The size of the file (in bytes)
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));
    
    /// <summary>
    /// The last modified time of the file
    /// </summary>
    public static readonly TimestampAttribute LastModified = new(Namespace, nameof(LastModified));

    /// <summary>
    /// The owning game installation
    /// </summary>
    public static readonly ReferenceAttribute<Sdk.Games.GameInstallMetadata> Game = new(Namespace, nameof(Game));
    
    
    public partial struct ReadOnly
    {
        /// <summary>
        /// The identity of the file. A row written before identities carried their kind has only the xxHash3,
        /// which is what such a row held.
        /// </summary>
        public FileIdentity IdentityOf()
        {
            if (DiskStateEntry.Identity.TryGetValue(this, out var identity)) return identity;
            return FileIdentity.XxHash3(DiskStateEntry.XxHash3.Get(this));
        }
    }
}
