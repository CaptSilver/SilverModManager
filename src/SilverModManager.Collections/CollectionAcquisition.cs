using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>
/// A file a collection download fetches. It is for the game of the loadout the collection is being installed into,
/// not for the Nexus Mods domain of its mods, which several games can share.
/// </summary>
[PublicAPI]
public sealed record CollectionAcquisition(LoadoutId Target) : IAcquisitionContext
{
    /// <inheritdoc />
    public Destination Destination(IDb db) => new LoadoutAcquisition(Target).Destination(db);
}
