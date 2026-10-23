using DynamicData.Kernel;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages.LoadoutGroupFilesPage;

/// <summary>
/// Finds the <see cref="GameInstalledClaim"/> behind a file on the External Changes page, so the page can offer
/// to give the file back to the game.
/// </summary>
internal static class GameInstalledClaimLookup
{
    public static Optional<GameInstalledClaimId> Find(IDb db, LoadoutId loadoutId, GamePath path)
    {
        foreach (var claim in GameInstalledClaim.FindByLoadout(db, loadoutId))
        {
            if ((GamePath)claim.Path == path) return GameInstalledClaimId.From(claim.Id);
        }

        return Optional<GameInstalledClaimId>.None;
    }
}
