using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077;

public class RunCyberpunk2077Game(IServiceProvider provider, Cyberpunk2077Game game) : RunGameTool<Cyberpunk2077Game>(provider, game)
{
    public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var spec = await base.GetLaunchSpec(loadout, cancellationToken);
        return spec with { ExtraArgs = [..spec.ExtraArgs, "-modded"] };
    }
}
