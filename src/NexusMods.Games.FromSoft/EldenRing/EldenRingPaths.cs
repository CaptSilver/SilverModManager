using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// The path rules for Elden Ring, as pure functions. Everything that needs to know where the game's folder, its
/// saves or the app's own files are asks here, so nobody else spells the names.
/// </summary>
public static class EldenRingPaths
{
    public const uint SteamAppId = 1245620;

    /// <summary>The folder inside the Steam install folder that holds the game's files.</summary>
    public const string GameSubfolder = "Game";

    /// <summary>The save file the game writes.</summary>
    public const string VanillaSave = "ER0000.sl2";

    /// <summary>The save file Seamless Co-op writes.</summary>
    public const string SeamlessCoopSave = "ER0000.co2";

    /// <summary>The save file a modded session is pointed at, so it never opens <see cref="VanillaSave"/>.</summary>
    public const string ModdedSave = "ER0000_silver.sl2";

    /// <summary>The folder with the game's executable and archives, <c>&lt;install&gt;/Game</c>.</summary>
    public static AbsolutePath GameFolder(GameInstallation installation) => installation.Locations[LocationId.Game].Path / GameSubfolder;

    /// <summary>The folder the game keeps one Steam account's saves in, inside a Proton prefix.</summary>
    public static AbsolutePath SaveFolder(AbsolutePath prefixPath, ulong steamId64)
        => prefixPath / "drive_c/users/steamuser/AppData/Roaming/EldenRing" / steamId64.ToString();

    /// <summary>The folder every Elden Ring installation's host-side files live under.</summary>
    public static AbsolutePath Me3Root(FromSoftSettings settings, IFileSystem fileSystem)
        => settings.DataRoot.ToPath(fileSystem) / "EldenRing";

    /// <summary>The folder mod packages and natives of one installation deploy to.</summary>
    public static AbsolutePath Me3Mods(FromSoftSettings settings, IFileSystem fileSystem, string storeIdentifier)
        => Me3Root(settings, fileSystem) / storeIdentifier / "mods";

    /// <summary>The folder the mod loader of one installation deploys to.</summary>
    public static AbsolutePath Me3Host(FromSoftSettings settings, IFileSystem fileSystem, string storeIdentifier)
        => Me3Root(settings, fileSystem) / storeIdentifier / "me3";

    /// <summary>
    /// Whether the path is inside the <c>Game</c> folder, which the app never writes to. Compared without regard
    /// to case, as Windows would.
    /// </summary>
    public static bool IsInGameFolder(GamePath path)
        => path.LocationId == LocationId.Game && path.Path.Path.StartsWith(GameSubfolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether indexing skips the path: the game's archives (<c>*.bdt</c>, <c>*.bhd</c>, <c>sd/</c>, <c>movie*/</c>)
    /// add up to tens of gigabytes that are never read. The small files beside them stay indexed, so a stray DLL or a
    /// swapped launcher is still seen.
    /// </summary>
    public static bool IsArchiveOrMedia(GamePath path)
    {
        if (!IsInGameFolder(path)) return false;

        var inside = path.Path.Path.AsSpan(GameSubfolder.Length + 1);
        if (inside.EndsWith(".bdt", StringComparison.OrdinalIgnoreCase) || inside.EndsWith(".bhd", StringComparison.OrdinalIgnoreCase)) return true;

        var slash = inside.IndexOf('/');
        if (slash < 0) return false;

        var folder = inside[..slash];
        return folder.Equals("sd", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("movie", StringComparison.OrdinalIgnoreCase);
    }
}
