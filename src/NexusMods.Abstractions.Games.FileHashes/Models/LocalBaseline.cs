using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Games.FileHashes.Models;

/// <summary>
/// The vanilla file list of one installation, built from the game's own install data and kept in the
/// local store. It is found again by the installation's store and path, because the local store cannot
/// reference entities of the main store.
/// </summary>
public partial class LocalBaseline : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.FileHashes.LocalBaseline";

    /// <summary>
    /// The store the installation is from, as <see cref="GameInstallMetadata.Store"/> holds it.
    /// </summary>
    public static readonly GameStoreAttribute Store = new(Namespace, nameof(Store)) { IsIndexed = true };

    /// <summary>
    /// The installation directory, as <see cref="GameInstallMetadata.Path"/> holds it. The query compares
    /// the two as strings, so they must be the same attribute type.
    /// </summary>
    public static readonly StringAttribute GamePath = new(Namespace, nameof(GamePath)) { IsIndexed = true };

    /// <summary>
    /// The installation's locator ids when the baseline was built, to tell whether it has gone stale.
    /// </summary>
    public static readonly StringsAttribute LocatorIds = new(Namespace, nameof(LocatorIds));

    /// <summary>
    /// Where the file list came from.
    /// </summary>
    public static readonly EnumByteAttribute<LocalBaselineKind> Kind = new(Namespace, nameof(Kind));

    /// <summary>
    /// The store's build id, or empty for a snapshot.
    /// </summary>
    public static readonly StringAttribute VersionName = new(Namespace, nameof(VersionName));

    /// <summary>
    /// When the baseline was built.
    /// </summary>
    public static readonly TimestampAttribute BuiltAt = new(Namespace, nameof(BuiltAt));

    /// <summary>
    /// Every file classified as vanilla. The hash rows are in the same store.
    /// </summary>
    public static readonly ReferencesAttribute<PathHashRelation> Files = new(Namespace, nameof(Files));

    /// <summary>
    /// The manifest rows written for a depot-manifest baseline, so lookups by locator id find it too.
    /// </summary>
    public static readonly ReferencesAttribute<SteamManifest> Manifests = new(Namespace, nameof(Manifests));
}
