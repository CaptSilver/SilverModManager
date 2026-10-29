
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.BaldursGate3;

public static class Bg3Constants
{
    public static readonly Extension PakFileExtension = new(".pak");
    
    public static readonly LocationId ModsLocationId = LocationId.From("Mods");

    public static readonly LocationId PlayerProfilesLocationId = LocationId.From("PlayerProfiles");

    public static readonly LocationId ScriptExtenderConfigLocationId = LocationId.From("ScriptExtenderConfig");

    /// <summary>What a player calls BG3's locations, keyed by id string so reading a name never needs the fields above.</summary>
    public static readonly IReadOnlyDictionary<string, string> LocationNames = new Dictionary<string, string>
    {
        ["Mods"] = "Mods",
        ["PlayerProfiles"] = "Profiles",
        ["ScriptExtenderConfig"] = "Script Extender settings",
    };

    public static readonly GamePath BG3SEGamePath = new(LocationId.Game, "bin/DWrite.dll");
}
