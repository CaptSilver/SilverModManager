using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// A job to create a loadout
/// </summary>
public record CreateLoadoutJob(GameInstallation Installation) : IJobDefinition<Loadout.ReadOnly>;
