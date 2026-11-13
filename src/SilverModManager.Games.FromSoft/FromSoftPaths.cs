using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft;

/// <summary>
/// The path rules of a FromSoft game, as pure functions of its <see cref="FromSoftGameLayout"/>. Everything that needs
/// to know where the game's folder, its saves or the app's own files are asks here, so nobody else spells the names.
/// </summary>
public static class FromSoftPaths
{
    /// <summary>The folder with the game's executable and archives, <c>&lt;install&gt;/&lt;GameSubfolder&gt;</c>, or the install folder itself when the subfolder is empty.</summary>
    public static AbsolutePath GameFolder(FromSoftGameLayout layout, GameInstallation installation)
        => GameFolderIn(layout, installation.Locations[LocationId.Game].Path);

    /// <summary>The game folder of the install folder at <paramref name="installFolder"/>.</summary>
    public static AbsolutePath GameFolderIn(FromSoftGameLayout layout, AbsolutePath installFolder)
        => layout.GameSubfolder.Length == 0 ? installFolder : installFolder / layout.GameSubfolder;

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

    /// <summary>The path of a file inside the game folder, as the game's file tree names it.</summary>
    public static GamePath InGameFolder(FromSoftGameLayout layout, string inside)
        => new(LocationId.Game, layout.GameSubfolder.Length == 0 ? inside : $"{layout.GameSubfolder}/{inside}");

    /// <summary>
    /// The part of the path inside the game folder, or null when the path is somewhere else. Compared without regard to
    /// case, as Windows would. With the game in the install root every <see cref="LocationId.Game"/> path is inside,
    /// and a path of any other location never is.
    /// </summary>
    public static string? InsideGameFolder(FromSoftGameLayout layout, GamePath path)
    {
        if (path.LocationId != LocationId.Game) return null;

        var full = path.Path.Path;
        if (layout.GameSubfolder.Length == 0) return full;

        return full.StartsWith(layout.GameSubfolder + "/", StringComparison.OrdinalIgnoreCase) ? full[(layout.GameSubfolder.Length + 1)..] : null;
    }

    /// <summary>Whether the path is inside the game's folder, which only the confirmed cleanup writes to.</summary>
    public static bool IsInGameFolder(FromSoftGameLayout layout, GamePath path) => InsideGameFolder(layout, path) is not null;

    /// <summary>
    /// Whether indexing skips the path: the game's archives (<c>*.bdt</c>, <c>*.bhd</c>, the layout's sound folder,
    /// <c>movie*/</c>) add up to tens of gigabytes that are never read. The small files beside them stay indexed, so a
    /// stray DLL or a swapped launcher is still seen. The regulation is exempt even when its name looks like an archive
    /// (a game that keeps it in <c>Data0.bdt</c>), because it is the file the game's checks hash.
    /// </summary>
    public static bool IsArchiveOrMedia(FromSoftGameDefinition definition, GamePath path)
    {
        var layout = definition.Layout;
        if (InsideGameFolder(layout, path) is not { } insideText) return false;

        var inside = insideText.AsSpan();
        if (definition.Formats.RegulationFileName is { } regulation && inside.Equals(regulation, StringComparison.OrdinalIgnoreCase)) return false;
        if (inside.EndsWith(".bdt", StringComparison.OrdinalIgnoreCase) || inside.EndsWith(".bhd", StringComparison.OrdinalIgnoreCase)) return true;

        var slash = inside.IndexOf('/');
        if (slash < 0) return false;

        var soundFolder = layout.SoundFolder switch
        {
            SoundFolderKind.Sd => "sd",
            SoundFolderKind.Sound => "sound",
            _ => throw new ArgumentOutOfRangeException(nameof(definition), layout.SoundFolder, "Unknown sound folder kind"),
        };
        var folder = inside[..slash];
        return folder.Equals(soundFolder, StringComparison.OrdinalIgnoreCase) || folder.StartsWith("movie", StringComparison.OrdinalIgnoreCase);
    }
}
