using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>
/// Where the overlay of one loadout on one installation keeps its folders. The lower folder is the real game
/// folder and is only ever read; everything the game writes during a session lands in <see cref="Upper"/>.
/// </summary>
/// <param name="Lower">The real <c>Game</c> folder.</param>
/// <param name="Root">The folder that holds everything of this overlay: upper, work and the audit state.</param>
public sealed record OverlayLayout(AbsolutePath Lower, AbsolutePath Root)
{
    /// <summary>Where the game's writes land, and where the configuration layer is written before a session.</summary>
    public AbsolutePath Upper => Root / "upper";

    /// <summary>The scratch folder overlayfs needs beside the upper folder, on the same file system.</summary>
    public AbsolutePath Work => Root / "work";

    /// <summary>What the last preparation wrote and saw, for the audit after the session.</summary>
    public AbsolutePath SessionState => Root / "session.json";

    /// <summary>What the last audit found and the user has not resolved yet.</summary>
    public AbsolutePath Findings => Root / "findings.json";

    /// <summary>
    /// The layout under the FromSoft data root: <c>&lt;root&gt;/EldenRing/&lt;installation&gt;/overlay/&lt;loadout&gt;</c>. The data root is
    /// outside every game folder, and one folder per loadout keeps a second loadout's leftovers out of this one's game.
    /// </summary>
    public static OverlayLayout For(FromSoftSettings settings, IFileSystem fileSystem, GameInstallation installation, LoadoutId loadoutId)
        => new(EldenRingPaths.GameFolder(installation), OverlayFolder(settings, fileSystem, installation) / loadoutId.Value.Value.ToString());

    /// <summary>The layouts of every loadout that has run on the installation, for a check that is not about one loadout.</summary>
    public static IReadOnlyList<OverlayLayout> All(FromSoftSettings settings, IFileSystem fileSystem, GameInstallation installation)
    {
        var folder = OverlayFolder(settings, fileSystem, installation);
        if (!folder.DirectoryExists()) return [];

        var lower = EldenRingPaths.GameFolder(installation);
        return folder.EnumerateDirectories(recursive: false).Select(root => new OverlayLayout(lower, root)).ToArray();
    }

    private static AbsolutePath OverlayFolder(FromSoftSettings settings, IFileSystem fileSystem, GameInstallation installation)
        => EldenRingPaths.Me3Root(settings, fileSystem) / EldenRingPaths.InstallationFolderName(installation.LocatorResult) / "overlay";
}
