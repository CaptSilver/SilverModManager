using NexusMods.Games.FromSoft.Saves;

namespace NexusMods.Games.FromSoft;

/// <summary>
/// What differs between FromSoft games, as plain values. A game class holds one in a static field and hands it to the
/// base constructor: a class cannot carry a static field and an instance property of the same name, so the record is
/// what both sides share. Members are <c>required</c>, so a game that lacks one fails to compile at its own
/// initializer.
/// </summary>
/// <remarks>Anything that needs the service provider is built in the game's constructor, not here.</remarks>
public sealed record FromSoftGameDefinition
{
    public required FromSoftGameLayout Layout { get; init; }

    public required FromSoftSaveNames SaveNames { get; init; }
}
