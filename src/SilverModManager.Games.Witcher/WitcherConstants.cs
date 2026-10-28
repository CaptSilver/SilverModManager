using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher;

public static class WitcherConstants
{
    /// <summary>Documents/The Witcher 3, where the game keeps mods.settings, its settings files and the saves.</summary>
    public static readonly LocationId WitcherDocuments = LocationId.From("WitcherDocuments");

    public const string W3ModsFolder = "Mods";
    public const string W3DlcFolder = "dlc";
    public const string W3MergedFolder = "mod0000_MergedFiles";
    public const string W3GameSaves = "gamesaves";

    public static readonly GamePath W3ModsSettings = new(WitcherDocuments, "mods.settings");
    public static readonly GamePath W3MenuConfigFolder = new(LocationId.Game, "bin/config/r4game/user_config_matrix/pc");
    public static readonly string[] W3SettingsFiles = ["input.settings", "user.settings", "dx12user.settings"];
}
