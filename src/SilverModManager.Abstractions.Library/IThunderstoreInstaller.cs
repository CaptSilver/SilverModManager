using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// The one way a Thunderstore package gets into a loadout, shared by the install verb, the Thunderstore page and the
/// ror2mm link handler.
/// </summary>
public interface IThunderstoreInstaller
{
    /// <summary>
    /// The communities Thunderstore lists the package under, empty when it does not know the package.
    /// </summary>
    ValueTask<IReadOnlyList<string>> CommunitiesOf(ThunderstorePackageRef package, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads <paramref name="root"/> with the packages it needs and installs or enables each one in the loadout.
    /// A modpack takes the dependency versions it lists (<see cref="DependencyPolicy.Exact"/>), anything else the latest.
    /// </summary>
    /// <exception cref="InvalidOperationException">The loadout's game has no Thunderstore support; the message names the game.</exception>
    /// <exception cref="ThunderstorePackageInDisabledGroupException">A package is only linked inside a disabled group.</exception>
    ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>> InstallAsync(ThunderstoreVersionRef root, LoadoutId loadoutId, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="InstallAsync(ThunderstoreVersionRef, LoadoutId, CancellationToken)"/> with the dependency policy
    /// chosen by the caller instead of the modpack rule.
    /// </summary>
    ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>> InstallAsync(ThunderstoreVersionRef root, LoadoutId loadoutId, DependencyPolicy policy, CancellationToken cancellationToken);
}

/// <summary>
/// What an install did with one package: the library item it is, whether this call downloaded it, and what happened
/// to it in the loadout.
/// </summary>
public readonly record struct ThunderstoreInstallOutcome(ThunderstoreVersionRef Version, LibraryItem.ReadOnly Item, bool Downloaded, LoadoutPackageOutcome Outcome);
