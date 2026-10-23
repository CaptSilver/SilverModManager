using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.Games;

namespace NexusMods.Wabbajack;

/// <summary>
/// The games a modlist can be built for, by the name Wabbajack's <c>Game</c> enum gives them (the modlist's
/// <c>GameType</c>), and the title and Nexus Mods domain each one is in this app. Kept as explicit tables: the names
/// belong to Wabbajack and the ids to this app, and neither can be derived from the other.
/// </summary>
public static class WabbajackGames
{
    private static readonly Dictionary<string, (GameId Id, GameDomain Domain)> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Morrowind"] = (GameId.From("CreationEngine.Morrowind"), GameDomain.From("morrowind")),
        ["Oblivion"] = (GameId.From("CreationEngine.Oblivion"), GameDomain.From("oblivion")),
        ["Fallout3"] = (GameId.From("CreationEngine.Fallout3"), GameDomain.From("fallout3")),
        ["FalloutNewVegas"] = (GameId.From("CreationEngine.FalloutNewVegas"), GameDomain.From("newvegas")),
        ["Skyrim"] = (GameId.From("CreationEngine.SkyrimLE"), GameDomain.From("skyrim")),
        ["Enderal"] = (GameId.From("CreationEngine.Enderal"), GameDomain.From("enderal")),
        ["SkyrimSpecialEdition"] = (GameId.From("CreationEngine.SkyrimSE"), GameDomain.From("skyrimspecialedition")),
        ["Fallout4"] = (GameId.From("CreationEngine.Fallout4"), GameDomain.From("fallout4")),
        // The VR editions are hosted under their flat-screen sibling's domain on Nexus Mods.
        ["SkyrimVR"] = (GameId.From("CreationEngine.SkyrimVR"), GameDomain.From("skyrimspecialedition")),
        ["Fallout4VR"] = (GameId.From("CreationEngine.Fallout4VR"), GameDomain.From("fallout4")),
        ["EnderalSpecialEdition"] = (GameId.From("CreationEngine.EnderalSE"), GameDomain.From("enderalspecialedition")),
        ["Starfield"] = (GameId.From("CreationEngine.Starfield"), GameDomain.From("starfield")),
        ["OblivionRemastered"] = (GameId.From("CreationEngine.OblivionRemastered"), GameDomain.From("oblivionremastered")),
    };

    /// <summary>The game of this app a modlist's <c>GameType</c> names, or null for a game it has no title for.</summary>
    public static GameId? GameIdFor(string gameType) => Games.TryGetValue(gameType, out var game) ? game.Id : null;

    /// <summary>The Nexus Mods domain the modlist's archives for <paramref name="gameType"/> are listed under.</summary>
    public static GameDomain? NexusDomainFor(string gameType) => Games.TryGetValue(gameType, out var game) ? game.Domain : null;
}
