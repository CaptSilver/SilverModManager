using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// The launch tool of the game. It refuses until the overlay launcher exists: the base tool would start the
/// primary file in the game folder, and a modded game must not be started that way.
/// </summary>
public class RunEldenRingGame(IServiceProvider provider, EldenRingGame game) : RunGameTool<EldenRingGame>(provider, game)
{
    public override ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => ValueTask.FromException<LaunchSpec>(new InvalidOperationException("Elden Ring is launched through the overlay launcher, which is not available yet"));
}
