using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Overlay;
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

    /// <summary>What the game folder checks look for: the anti-cheat launcher, mod folders and item spawners of this game.</summary>
    public GameFolderCheckRules CheckRules { get; init; } = GameFolderCheckRules.ModEngine2;

    /// <summary>What a session may write into the overlay without being flagged. Defaults to file name patterns only.</summary>
    public OverlayAllowlist Overlay { get; init; } = OverlayAllowlist.Default;
}
