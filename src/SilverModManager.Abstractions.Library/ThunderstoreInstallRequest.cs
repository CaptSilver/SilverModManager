using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Abstractions.Library;

/// <summary>
/// What to add to the library from Thunderstore.
/// </summary>
/// <param name="Root">The version being installed.</param>
/// <param name="Game">The game it is installed for. Carries the community and the mod loader package, and a game without Thunderstore support cannot be passed.</param>
/// <param name="WithDependencies">Whether to add the root's dependencies too, or only the root.</param>
/// <param name="Policy">Which versions of the dependencies to take.</param>
/// <param name="ExpectedSha256">The digest the root's zip must have. Dependencies are never checked against it.</param>
/// <param name="InstalledInLoadout">Packages already in the loadout with their versions; those are not added again.</param>
public record ThunderstoreInstallRequest(
    ThunderstoreVersionRef Root,
    IThunderstoreGame Game,
    bool WithDependencies,
    DependencyPolicy Policy,
    Sha256Value? ExpectedSha256,
    IReadOnlyDictionary<ThunderstorePackageRef, ThunderstoreVersionNumber> InstalledInLoadout);
