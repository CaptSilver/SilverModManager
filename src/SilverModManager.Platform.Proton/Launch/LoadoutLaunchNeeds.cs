using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Deployment;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Whether a loadout's mods only exist when the game is started through the app. Linked and copied files are
/// real files in the game folder and work however the game is launched; a mount or an overlay is not.
/// </summary>
public static class LoadoutLaunchNeeds
{
    public static bool NeedsApp(DeploymentPlan plan, LaunchSpec? spec) => plan.UsesMount || spec is { FilesOnlyInLaunch: true };
}
