using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Emitters;

/// <summary>
/// Warns that a game with one mod list per player profile has several profiles and none is picked, which is
/// the case where applying the loadout is refused.
/// </summary>
/// <param name="settingValue">Reads the profile the player picked, each time, so a change in the settings shows at once.</param>
public sealed class LarianProfileEmitter(ILarianGame game, Func<string> settingValue) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var layout = game.Profile.Profiles;
        if (layout is not LarianProfileLayout.PerProfileFolder) yield break;

        var profiles = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LarianConstants.PlayerProfilesLocationId, ""));
        if (LarianProfileResolver.CountProfiles(profiles) < 2) yield break;
        if (LarianProfileResolver.Resolve(layout, profiles, settingValue()) is not null) yield break;

        yield return Diagnostics.CreateLarianProfileUnset(GameName: game.Profile.GameName);
    }
}
