using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

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
