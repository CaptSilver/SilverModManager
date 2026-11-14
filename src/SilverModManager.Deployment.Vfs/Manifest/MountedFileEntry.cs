using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// A file a mounted folder serves instead of the real folder's, or a path it hides. The rows of one root are the
/// mount's manifest: the synchronizer lays them over the real folder's state, and the mount reads them to build its tree.
/// </summary>
public partial class MountedFileEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Deployment.MountedFileEntry";

    /// <summary>The installation the mounted folder belongs to.</summary>
    public static readonly ReferenceAttribute<GameInstallMetadata> Installation = new(Namespace, nameof(Installation)) { IsIndexed = true };

    /// <summary>The mounted folder. The parent is the installation id, same shape as <c>DeploymentPlanEntry.Root</c>.</summary>
    public static readonly GamePathParentAttribute Root = new(Namespace, nameof(Root));

    /// <summary>Where the mount presents the file.</summary>
    public static readonly GamePathParentAttribute Path = new(Namespace, nameof(Path));

    /// <summary>
    /// The identity of the served file, which lives in the loose store under its xxHash3. Absent on a hidden row,
    /// which serves nothing.
    /// </summary>
    public static readonly FileIdentityAttribute Identity = new(Namespace, nameof(Identity)) { IsOptional = true };

    /// <summary>The size of the served file. Absent exactly when <see cref="Identity"/> is.</summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size)) { IsOptional = true };

    /// <summary>
    /// The identity of the real file this row shadows, absent when there was none. The disk state keeps whatever
    /// kind the re-index measured, so this is not necessarily an xxHash3.
    /// </summary>
    public static readonly FileIdentityAttribute BackingIdentity = new(Namespace, nameof(BackingIdentity)) { IsOptional = true };

    /// <summary>True when the mount shows nothing at the path although the real folder has a file there.</summary>
    public static readonly BooleanAttribute Hidden = new(Namespace, nameof(Hidden)) { IsOptional = true };

    /// <summary>The modification time the mount serves, when the game needs one.</summary>
    public static readonly TimestampAttribute MTime = new(Namespace, nameof(MTime)) { IsOptional = true };
}
