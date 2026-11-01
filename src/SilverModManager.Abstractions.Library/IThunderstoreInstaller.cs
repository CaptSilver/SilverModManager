using System.Diagnostics.CodeAnalysis;
using SilverModManager.Sdk.Games;
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
    /// The Thunderstore side of the loadout's game. <paramref name="problem"/> says why not when the loadout's game
    /// has no Thunderstore support, naming the game when it is known. The game comes from the installation's recorded
    /// id, not the game registry, so a game that went missing does not fail the lookup.
    /// </summary>
    bool TryGetGame(LoadoutId loadoutId, [NotNullWhen(true)] out IThunderstoreGame? game, out string problem);

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

    /// <summary>
    /// Moves every loadout that links <paramref name="current"/> to <paramref name="newest"/>. Downloads
    /// <paramref name="newest"/> and, for each loadout the replace will touch (links outside read-only collections),
    /// the dependencies that loadout lacks, which it installs or enables there before the root changes. Then it
    /// swaps the root once for all of them, so the new version takes the old one's place instead of being linked
    /// beside it, and is switched off again where the old one was switched off. Deletes nothing from the library: the
    /// old version stays there, and removing it is the caller's call. An item no loadout links is only replaced in
    /// the library.
    /// </summary>
    /// <exception cref="InvalidOperationException">No game serves the community of <paramref name="current"/>.</exception>
    /// <exception cref="ThunderstorePackageInDisabledGroupException">
    /// A dependency is only linked inside a disabled group. Nothing has been replaced by then.
    /// </exception>
    ValueTask<ModUpdateOutcome> UpdateAsync(ThunderstoreVersionRef newest, LibraryItem.ReadOnly current, CancellationToken cancellationToken);
}

/// <summary>
/// What an install did with one package: the library item it is, whether this call downloaded it, and what happened
/// to it in the loadout.
/// </summary>
public readonly record struct ThunderstoreInstallOutcome(ThunderstoreVersionRef Version, LibraryItem.ReadOnly Item, bool Downloaded, LoadoutPackageOutcome Outcome);
