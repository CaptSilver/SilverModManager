using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft;

/// <summary>
/// The path rules of a FromSoft game, as pure functions of its <see cref="FromSoftGameLayout"/>. Everything that needs
/// to know where the game's folder, its saves or the app's own files are asks here, so nobody else spells the names.
/// </summary>
public static class FromSoftPaths
{
    /// <summary>The folder with the game's executable and archives, <c>&lt;install&gt;/&lt;GameSubfolder&gt;</c>.</summary>
    public static AbsolutePath GameFolder(FromSoftGameLayout layout, GameInstallation installation)
        => installation.Locations[LocationId.Game].Path / layout.GameSubfolder;

    /// <summary>The folder the game keeps every account's saves in, inside a Proton prefix.</summary>
    public static AbsolutePath SaveRoot(FromSoftGameLayout layout, AbsolutePath prefixPath) => layout.SaveRoot switch
    {
        SaveRootKind.RoamingAppData => prefixPath / "drive_c/users/steamuser/AppData/Roaming" / layout.SaveFolderName,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.SaveRoot, "Unknown save root"),
    };

    /// <summary>The folder the game keeps one Steam account's saves in, inside a Proton prefix.</summary>
    public static AbsolutePath SaveFolder(FromSoftGameLayout layout, AbsolutePath prefixPath, ulong steamId64) => layout.AccountFolder switch
    {
        SaveAccountFolderKind.SteamId64Decimal => SaveRoot(layout, prefixPath) / steamId64.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.AccountFolder, "Unknown account folder kind"),
    };

    /// <summary>
    /// The name of an installation's folder under <see cref="DataRoot"/>: its Steam app id, and the game's own app id
    /// for an install that did not come from Steam.
    /// </summary>
    public static string InstallationFolderName(FromSoftGameLayout layout, GameLocatorResult locatorResult)
        => locatorResult.Store == GameStore.Steam ? locatorResult.StoreIdentifier : layout.SteamAppId.ToString();

    /// <summary>The folder every installation of the game keeps its host-side files under.</summary>
    public static AbsolutePath DataRoot(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem)
        => settings.DataRoot.ToPath(fileSystem) / layout.DataFolderName;

    /// <summary>The folder one installation's host-side files live under.</summary>
    public static AbsolutePath InstallationRoot(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => DataRoot(layout, settings, fileSystem) / installationFolder;

    /// <summary>The folder one installation's cleanup records live in, such as the links it removed from the game folder.</summary>
    public static AbsolutePath CleanupFolder(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => InstallationRoot(layout, settings, fileSystem, installationFolder) / "cleanup";

    /// <summary>
    /// Whether the path is inside the game's subfolder, which only the confirmed cleanup writes to. Compared without regard
    /// to case, as Windows would.
    /// </summary>
    public static bool IsInGameFolder(FromSoftGameLayout layout, GamePath path)
        => path.LocationId == LocationId.Game && path.Path.Path.StartsWith(layout.GameSubfolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether indexing skips the path: the game's archives (<c>*.bdt</c>, <c>*.bhd</c>, <c>sd/</c>, <c>movie*/</c>)
    /// add up to tens of gigabytes that are never read. The small files beside them stay indexed, so a stray DLL or a
    /// swapped launcher is still seen. The rule holds for every game that ships BHD5 archives.
    /// </summary>
    public static bool IsArchiveOrMedia(FromSoftGameLayout layout, GamePath path)
    {
        if (!IsInGameFolder(layout, path)) return false;

        var inside = path.Path.Path.AsSpan(layout.GameSubfolder.Length + 1);
        if (inside.EndsWith(".bdt", StringComparison.OrdinalIgnoreCase) || inside.EndsWith(".bhd", StringComparison.OrdinalIgnoreCase)) return true;

        var slash = inside.IndexOf('/');
        if (slash < 0) return false;

        var folder = inside[..slash];
        return folder.Equals("sd", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("movie", StringComparison.OrdinalIgnoreCase);
    }
}
