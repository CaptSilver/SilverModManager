using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// The one way a GameBanana mod gets into a loadout when only its ids are known, which is what a link on the site
/// carries. The catalog page installs through the same code from its cards.
/// </summary>
public interface IGameBananaInstaller
{
    /// <summary>
    /// GameBanana's id for the game the mod is listed under, or null when the mod is private, withheld or unknown.
    /// A link names no game, so the handler picks the loadout by this.
    /// </summary>
    ValueTask<int?> GameOf(long modId, CancellationToken cancellationToken);

    /// <summary>
    /// Installs the mod's file into the loadout. <paramref name="fileId"/> is the file the link names, or null for
    /// the mod's only file. A deliberate refusal comes back as <see cref="CatalogInstallOutcome.Refused"/>.
    /// </summary>
    Task<CatalogInstallOutcome> Install(long modId, long? fileId, LoadoutId loadout, CancellationToken cancellationToken);
}
