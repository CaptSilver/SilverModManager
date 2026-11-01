using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// What an acquisition knows about where an item goes. Not called DestinationGames: that is the attribute, and the
/// generated LibraryItem.ReadOnly.DestinationGames property would shadow a type of the same name.
/// </summary>
[PublicAPI]
public sealed record Destination(IReadOnlySet<GameId> Games, bool Shared)
{
    /// <summary>No game is known.</summary>
    public static readonly Destination Unknown = new(ImmutableHashSet<GameId>.Empty, Shared: false);

    /// <summary>Exactly one game, by the item's own acquisition or install.</summary>
    public static Destination Of(GameId game) => new(ImmutableHashSet.Create(game), Shared: false);

    /// <summary>True when no game is named.</summary>
    public bool IsUnknown => Games.Count == 0;

    // A record compares IReadOnlySet by reference; two destinations with the same games must be equal.
    /// <inheritdoc />
    public bool Equals(Destination? other) => other is not null && Shared == other.Shared && Games.SetEquals(other.Games);

    /// <inheritdoc />
    public override int GetHashCode() => Games.Aggregate(Shared.GetHashCode(), (h, g) => h ^ g.GetHashCode());
}
