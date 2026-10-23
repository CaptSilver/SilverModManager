using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Games.Modlists;

/// <summary>
/// What a game says about the Mod Organizer 2 instance a Wabbajack modlist lays out: where a mod's files go, which
/// ini files a profile carries, which folder is a copy of the game, and how a profile's plugin lists become the
/// loadout's plugin order. A game that does not implement it cannot take a modlist.
/// </summary>
public interface IMo2InstanceTarget
{
    /// <summary>Why modlists for this game cannot be installed, or null when they can.</summary>
    string? UnsupportedReason(GameInstallation installation);

    /// <summary>Where a file at <paramref name="modRelative"/> inside an MO2 mod goes in the game.</summary>
    GamePath ModFilePath(GameInstallation installation, RelativePath modRelative);

    /// <summary>The ini files MO2 keeps in a profile, at the place and with the spelling the game uses for each.</summary>
    IReadOnlyList<GamePath> ProfileSettingsFiles(GameInstallation installation);

    /// <summary>Whether the top-level folder <paramref name="folderName"/>, holding <paramref name="filesDirectlyInside"/>, is a copy of the game folder.</summary>
    bool IsGameRootFolder(GameInstallation installation, string folderName, IReadOnlySet<RelativePath> filesDirectlyInside);

    /// <summary>
    /// Writes the order and switches of a profile's plugins.txt and loadorder.txt into <paramref name="tx"/>, on the
    /// loadout's own plugin order <paramref name="sortOrderId"/> (never a collection's), and marks the order as
    /// filled so the first Apply does not replace it with the game's list. At least one of the texts is needed.
    /// </summary>
    void StagePluginList(Loadout.ReadOnly loadout, SortOrderId sortOrderId, string? pluginsTxt, string? loadorderTxt, ITransaction tx);
}
