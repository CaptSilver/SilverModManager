using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Starters;

/// <summary>
/// How long the waits of a launch run. The production values are the defaults of <see cref="RunGameTool"/>;
/// tests shorten them.
/// </summary>
/// <param name="StartTimeout">How long Steam's reaper for the game gets to show up once Steam is running.</param>
/// <param name="SteamStartTimeout">How long Steam itself gets to be running after the URI is opened.</param>
/// <param name="PollInterval">How often the process table is looked at.</param>
/// <param name="Settle">How long a program the app started itself must stay gone to count as finished.</param>
/// <param name="SteamSettle">The same for a game Steam started.</param>
/// <param name="Clock">The clock the waits run on.</param>
public sealed record StartTiming(TimeSpan StartTimeout, TimeSpan SteamStartTimeout, TimeSpan PollInterval, TimeSpan Settle, TimeSpan SteamSettle, TimeProvider Clock);

/// <summary>
/// Everything a starter needs to start one game. <paramref name="CallerArgs"/> are the arguments
/// the tool's <c>Execute</c> was given; <c>Spec.ExtraArgs</c> are the tool's own. The direct starter runs
/// both; the process starter refuses <paramref name="CallerArgs"/> only, because <c>Spec.ExtraArgs</c> is the
/// sandbox's argv there.
/// </summary>
public sealed record GameStartRequest(Loadout.ReadOnly Loadout, GameInstallation Installation, LaunchSpec Spec, string[] CallerArgs, bool IsPrimaryExe, bool UseShell, StartTiming Timing)
{
    /// <summary>The caller's arguments followed by the tool's own.</summary>
    public string[] AllArgs => [..CallerArgs, ..Spec.ExtraArgs];

    /// <summary>The game's name for messages.</summary>
    public string GameName => Installation.Game.DisplayName;
}
