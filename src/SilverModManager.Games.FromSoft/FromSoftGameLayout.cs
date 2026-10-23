namespace SilverModManager.Games.FromSoft;

/// <summary>Where a game keeps its saves on the Proton prefix's drive.</summary>
public enum SaveRootKind
{
    /// <summary><c>drive_c/users/steamuser/AppData/Roaming/&lt;SaveFolderName&gt;</c>.</summary>
    RoamingAppData,
}

/// <summary>How the per-account folder under a game's save root is named.</summary>
public enum SaveAccountFolderKind
{
    /// <summary>The account's SteamID64 in decimal.</summary>
    SteamId64Decimal,
}

/// <summary>
/// The names a FromSoft game is laid out by: its Steam app id, where its files and saves are and what the app's own
/// per-game folder is called. Everything that used to spell one of Elden Ring's names takes one of these.
/// </summary>
/// <param name="SteamAppId">The game's Steam app id; also the name of an installation's folder when it did not come from Steam.</param>
/// <param name="DataFolderName">The folder under <see cref="FromSoftSettings.DataRoot"/> that holds every installation of the game.</param>
/// <param name="GameSubfolder">The folder inside the Steam install folder that holds the game's files. Never empty.</param>
/// <param name="GameExecutable">The game's own executable, in <paramref name="GameSubfolder"/>.</param>
/// <param name="Launcher">The anti-cheat launcher Steam starts, in <paramref name="GameSubfolder"/>; <c>null</c> when the game has none and Steam starts the executable.</param>
/// <param name="SaveFolderName">The game's folder under the save root.</param>
/// <param name="SaveRoot">Which folder of the prefix the saves are under.</param>
/// <param name="AccountFolder">How the per-account folder under the game's save folder is named.</param>
public sealed record FromSoftGameLayout(
    uint SteamAppId,
    string DataFolderName,
    string GameSubfolder,
    string GameExecutable,
    string? Launcher,
    string SaveFolderName,
    SaveRootKind SaveRoot,
    SaveAccountFolderKind AccountFolder)
{
    /// <summary>
    /// An empty subfolder would make <see cref="FromSoftPaths.IsInGameFolder"/> match nothing and so switch the
    /// never-written guard off without a sound. A game whose executable sits in the install root needs that guard
    /// designed for it, so it fails here until then.
    /// </summary>
    public string GameSubfolder { get; } = string.IsNullOrWhiteSpace(GameSubfolder)
        ? throw new ArgumentException("A FromSoft game's files must be in a subfolder of its install folder.", nameof(GameSubfolder))
        : GameSubfolder;
}
