using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// One piece of game-installed content a recognizer found.
/// </summary>
/// <param name="Source">Persisted source name, see <see cref="GameInstalledItem.Source"/>.</param>
/// <param name="SourceId">Id within the source, see <see cref="GameInstalledItem.SourceId"/>.</param>
/// <param name="Title">Display name of the item.</param>
/// <param name="Version">Version, only when it parses as digits and dots.</param>
/// <param name="Confidence">How the recognizer knows the item is the game's.</param>
/// <param name="CanBeClaimed">Whether "Treat as my file" is offered.</param>
/// <param name="Files">The item's files that are on disk.</param>
/// <param name="Extra">The recognizer's own data about the item. Handed to
/// <c>WriteGameInstalledItemExtras</c> and read nowhere else.</param>
public sealed record GameInstalledContent(
    string Source,
    string SourceId,
    string Title,
    string? Version,
    GameInstalledConfidence Confidence,
    bool CanBeClaimed,
    IReadOnlyList<GamePath> Files,
    object? Extra = null);
