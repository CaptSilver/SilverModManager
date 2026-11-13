using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>
/// Location ids and file extensions every Larian game shares. The id strings are persisted in loadouts, so they
/// stay as Baldur's Gate 3 first wrote them.
/// </summary>
public static class LarianConstants
{
    public static readonly Extension PakFileExtension = new(".pak");

    public static readonly LocationId ModsLocationId = LocationId.From("Mods");

    public static readonly LocationId PlayerProfilesLocationId = LocationId.From("PlayerProfiles");

    public static readonly LocationId ScriptExtenderConfigLocationId = LocationId.From("ScriptExtenderConfig");
}
