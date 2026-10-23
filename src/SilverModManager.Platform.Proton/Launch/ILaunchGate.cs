using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The launch a gate is asked about.
/// </summary>
/// <param name="Installation">The game being launched.</param>
/// <param name="LoadoutId">The loadout being applied for it.</param>
/// <param name="LoadoutName">The loadout's name, for messages.</param>
/// <param name="WineDllOverrides">
/// The overrides the game needs for this loadout. They go into the state file so a Steam wrapper that joins
/// the launch can set them without opening the database.
/// </param>
/// <param name="RunSpec">
/// What the loadout's run tool wants started, for a launch whose game Steam starts. Its exe and arguments go into
/// the state file for the same reason. Null when the command is left as it is.
/// </param>
public sealed record LaunchContext(GameInstallation Installation, LoadoutId LoadoutId, string LoadoutName, IReadOnlyList<WineDllOverride> WineDllOverrides, LaunchSpec? RunSpec = null);

/// <summary>
/// A gate's reason for refusing a launch.
/// </summary>
/// <param name="Message">What the user is told. It is the whole message: the session adds nothing to it.</param>
public sealed record Veto(string Message);

/// <summary>
/// A step a launch takes part in without owning, such as a check that the game's prerequisites are in place.
/// Gates run in registration order before the game starts and again, in the same order, after it exits.
/// </summary>
public interface ILaunchGate
{
    /// <summary>
    /// Whether the launch may go ahead. Runs once the installation lock is held and before anything is applied,
    /// so it must not change the game folder.
    /// </summary>
    Veto? Check(LaunchContext context);

    /// <summary>
    /// Runs after the loadout has been applied and before the game starts. A throw fails the launch.
    /// </summary>
    Task Prepare(LaunchContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Runs after the game has exited, before its changes are ingested. Does not run when the game could not be
    /// shut down, because the game folder is still in use.
    /// </summary>
    Task Finish(LaunchContext context);
}
