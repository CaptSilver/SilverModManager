using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Sdk.Library;

/// <summary>An item acquired from inside a loadout: it is for that loadout's game.</summary>
[PublicAPI]
public sealed record LoadoutAcquisition(LoadoutId Loadout) : IAcquisitionContext
{
    /// <inheritdoc />
    /// <remarks>
    /// <see cref="GameInstallMetadata.Game"/> is optional; an installation without it (a database the key
    /// migration could not key) answers unknown.
    /// </remarks>
    public Destination Destination(IDb db)
    {
        var installation = Loadouts.Loadout.Load(db, Loadout).Installation;
        return GameInstallMetadata.Game.TryGetValue(installation, out var game)
            ? Library.Destination.Of(game)
            : Library.Destination.Unknown;
    }
}
