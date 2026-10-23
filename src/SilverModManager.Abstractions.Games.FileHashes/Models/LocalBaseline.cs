using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

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
    /// The game's own id, as <see cref="GameInstallMetadata.Game"/> holds it. Absent on baselines built
    /// before the game was recorded.
    /// </summary>
    public static readonly GameIdAttribute Game = new(Namespace, nameof(Game)) { IsOptional = true };

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
    /// The version of the rules that decided which files are vanilla, ignored or non-vanilla when the baseline
    /// was built. Absent on baselines built before the rules were versioned. A baseline built under older
    /// rules cannot be trusted, since a file the old rules skipped is in neither list.
    /// </summary>
    public static readonly Int32Attribute ClassificationVersion = new(Namespace, nameof(ClassificationVersion)) { IsOptional = true };

    /// <summary>
    /// Every file classified as vanilla. The hash rows are in the same store.
    /// </summary>
    public static readonly ReferencesAttribute<PathHashRelation> Files = new(Namespace, nameof(Files));

    /// <summary>
    /// The manifest rows written for a depot-manifest baseline, so lookups by locator id find it too.
    /// </summary>
    public static readonly ReferencesAttribute<SteamManifest> Manifests = new(Namespace, nameof(Manifests));
}
