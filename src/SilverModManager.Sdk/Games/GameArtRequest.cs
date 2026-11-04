using System.Collections.Immutable;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// What an <see cref="IGameArtSource"/> is asked to draw: the Steam ids to look art up by, plus the game's
/// own identity so a game with no Steam id (or no art anywhere) still gets a picture of its own.
/// </summary>
/// <param name="GameId">Stable identity; seeds the colour of a generated picture.</param>
/// <param name="DisplayName">The name shown to the user; its initials go on a generated picture.</param>
/// <param name="SteamAppIds">Steam ids to try in order; empty for a game Steam does not sell.</param>
[PublicAPI]
public sealed record GameArtRequest(GameId GameId, string DisplayName, ImmutableArray<uint> SteamAppIds);
