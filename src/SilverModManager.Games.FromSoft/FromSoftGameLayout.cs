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

/// <summary>Which folder of the game folder holds the sound banks that indexing skips.</summary>
public enum SoundFolderKind
{
    /// <summary><c>sd/</c>.</summary>
    Sd,

    /// <summary><c>sound/</c>.</summary>
    Sound,
}

/// <summary>
/// The names a FromSoft game is laid out by: its Steam app id, where its files and saves are and what the app's own
/// per-game folder is called. Everything that used to spell one of Elden Ring's names takes one of these.
/// </summary>
/// <param name="SteamAppId">The game's Steam app id; also the name of an installation's folder when it did not come from Steam.</param>
/// <param name="DataFolderName">The folder under <see cref="FromSoftSettings.DataRoot"/> that holds every installation of the game.</param>
/// <param name="GameSubfolder">The folder inside the Steam install folder that holds the game's files. <c>""</c> means the executable sits in the install root.</param>
/// <param name="GameExecutable">The game's own executable, in <paramref name="GameSubfolder"/>.</param>
/// <param name="Launcher">The anti-cheat launcher Steam starts, in <paramref name="GameSubfolder"/>; <c>null</c> when the game has none and Steam starts the executable.</param>
/// <param name="SaveFolderName">The game's folder under the save root.</param>
/// <param name="SaveRoot">Which folder of the prefix the saves are under.</param>
/// <param name="AccountFolder">How the per-account folder under the game's save folder is named.</param>
/// <param name="SoundFolder">The sound folder of the game folder, which indexing skips with the archives.</param>
public sealed record FromSoftGameLayout(
    uint SteamAppId,
    string DataFolderName,
    string GameSubfolder,
    string GameExecutable,
    string? Launcher,
    string SaveFolderName,
    SaveRootKind SaveRoot,
    SaveAccountFolderKind AccountFolder,
    SoundFolderKind SoundFolder)
{
    /// <summary>The folder inside the install folder that holds the game's files; <c>""</c> when the executable sits in the install root.</summary>
    public string GameSubfolder { get; } = GameSubfolder is { Length: > 0 } && string.IsNullOrWhiteSpace(GameSubfolder)
        ? throw new ArgumentException("A blank FromSoft game subfolder is a typo for the install root, which is written as an empty string.", nameof(GameSubfolder))
        : GameSubfolder;
}
