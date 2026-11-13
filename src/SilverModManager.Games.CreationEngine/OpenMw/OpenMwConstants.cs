using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// The locations of an OpenMW installation. <see cref="OpenMwConfig"/> keeps the string the Morrowind row stored
/// for it, because loadouts persist paths under it.
/// </summary>
public static class OpenMwConstants
{
    /// <summary>The folder that holds the user's openmw.cfg.</summary>
    public static readonly LocationId OpenMwConfig = LocationId.From("OpenMwConfig");

    /// <summary>OpenMW's <c>data-local</c> folder: the one data directory the engine loads last, which mods deploy into.</summary>
    public static readonly LocationId OpenMwDataLocal = LocationId.From("OpenMwDataLocal");

    public const string ConfigFileName = "openmw.cfg";

    public static readonly GamePath OpenMwConfigFile = new(OpenMwConfig, ConfigFileName);
}
