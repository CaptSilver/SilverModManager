
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.BaldursGate3;

public static class Bg3Constants
{
    public static readonly Extension PakFileExtension = new(".pak");
    
    public static readonly LocationId ModsLocationId = LocationId.From("Mods", "Mods folder");

    public static readonly LocationId PlayerProfilesLocationId = LocationId.From("PlayerProfiles", "Profiles");

    public static readonly LocationId ScriptExtenderConfigLocationId = LocationId.From("ScriptExtenderConfig", "Script Extender settings");

    public static readonly GamePath BG3SEGamePath = new(LocationId.Game, "bin/DWrite.dll");
}
