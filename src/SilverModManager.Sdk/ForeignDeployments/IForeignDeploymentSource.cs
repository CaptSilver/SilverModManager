using JetBrains.Annotations;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Sdk.ForeignDeployments;

/// <summary>
/// A deployment another mod manager left in a game folder. One implementation per manager: it recognises that
/// manager's records, and lists the mods the manager says it deployed and the files of each.
/// </summary>
/// <remarks>
/// Nothing here writes to the game folder or touches the database. The app reads the result when a game starts
/// being managed, adopts the files as mods, and keeps them out of the vanilla baseline.
/// </remarks>
[PublicAPI]
public interface IForeignDeploymentSource
{
    /// <summary>The manager's name as the player knows it, for example "Vortex".</summary>
    string ManagerName { get; }

    /// <summary>
    /// Whether the path is one of the manager's own record files (a manifest, a marker). Decided by name alone,
    /// without touching the disk. Such files stay on disk, and the app neither indexes nor ingests them, so they
    /// are never a mod's file and are never deleted when a mod is switched off.
    /// </summary>
    bool IsRecordFile(GamePath path);

    /// <summary>
    /// Whether the path is the marker file the manager drops into every folder it deploys to. A subset of
    /// <see cref="IsRecordFile"/>: routine clutter, so skipping one is not worth a warning.
    /// </summary>
    bool IsFolderMarker(GamePath path);

    /// <summary>
    /// Reads the manager's records in the game folder. Null when the manager left none.
    /// </summary>
    /// <exception cref="ForeignDeploymentRefusedException">A record names a file outside the game folder.</exception>
    ForeignDeployment? Detect(GameInstallation installation);
}

/// <summary>What one manager's records say is deployed in a game folder.</summary>
/// <param name="Manager"><see cref="IForeignDeploymentSource.ManagerName"/> of the source that found it.</param>
/// <param name="Mods">The mods, one per source mod the manager lists, with only the files that exist on disk.</param>
/// <param name="MissingFiles">Files a record lists that are not on disk. They are not adopted.</param>
/// <param name="ReplacedFiles">
/// Files a record lists that the manager put over one of the game's own, keeping the original beside them. Adopting one
/// would delete the game file when its mod is switched off with the original not recorded anywhere, so they are not adopted.
/// </param>
public sealed record ForeignDeployment(string Manager, IReadOnlyList<ForeignMod> Mods, IReadOnlyList<GamePath> MissingFiles, IReadOnlyList<GamePath>? ReplacedFiles = null)
{
    /// <summary><see cref="ReplacedFiles"/>, empty when the source has none.</summary>
    public IReadOnlyList<GamePath> Replaced => ReplacedFiles ?? [];

    /// <summary>Every file of every mod.</summary>
    public IEnumerable<GamePath> Files => Mods.SelectMany(mod => mod.Files);
}

/// <summary>One mod the other manager deployed.</summary>
/// <param name="Source">The manager's own name for the mod, kept whole so it can be matched against the manager's records later.</param>
/// <param name="DisplayName">What the player sees: <paramref name="Source"/> without the id, version and time the manager appends.</param>
/// <param name="NexusModId">The Nexus Mods id when <paramref name="Source"/> carries one. No network call is made for it.</param>
/// <param name="Version">The version text taken from <paramref name="Source"/>, if it has one.</param>
/// <param name="Files">The mod's files that exist on disk, relative to their game location.</param>
public sealed record ForeignMod(string Source, string DisplayName, ModId? NexusModId, string? Version, IReadOnlyList<GamePath> Files);

/// <summary>
/// A foreign manager's records were refused: they name a file outside the game folder, so the game was not added.
/// </summary>
[PublicAPI]
public sealed class ForeignDeploymentRefusedException(string message) : Errors.UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The game was not added";
}
