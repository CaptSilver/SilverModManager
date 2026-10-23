using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Lets the user take game-installed files over as their own, and give them back.
/// </summary>
public interface IGameInstalledContentService
{
    /// <summary>
    /// Treats the item's files as the user's own: they leave the game-installed group, are recorded as claimed
    /// and move to the overrides group at their disk hash. Runs in one transaction.
    /// </summary>
    Task<ClaimOutcome> Claim(GameInstalledItemId item, CancellationToken token = default);

    /// <summary>
    /// Undoes a claim so the recognizer owns the path again.
    /// </summary>
    Task<UnclaimOutcome> Unclaim(GameInstalledClaimId claim, CancellationToken token = default);
}

/// <summary>
/// Result of <see cref="IGameInstalledContentService.Claim"/>.
/// </summary>
/// <param name="MovedToExternalChanges">Paths that had no usable bytes to keep and went to External Changes.</param>
public sealed record ClaimOutcome(IReadOnlyList<GamePath> MovedToExternalChanges);

/// <summary>
/// Result of <see cref="IGameInstalledContentService.Unclaim"/>.
/// </summary>
/// <param name="Restored">Whether the claim was undone.</param>
/// <param name="Reason">Why it was not, when it was not.</param>
public sealed record UnclaimOutcome(bool Restored, string? Reason);
