using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// What started a sync. The log says it, so a folder that changed can be traced to a button, a launch or a command.
/// </summary>
[PublicAPI]
public enum SyncTrigger
{
    /// <summary>The Apply button, or a card's switch-to-this-loadout button.</summary>
    ApplyButton,

    /// <summary>Starting the game, which applies the loadout first.</summary>
    Launch,

    /// <summary>The game closed and its changes are taken back into the loadout.</summary>
    AfterGameRun,

    /// <summary>Running a tool other than the game.</summary>
    ToolRun,

    /// <summary>A <c>silvermod</c> command.</summary>
    CommandLine,

    /// <summary>The first sync of a game the app has just started managing.</summary>
    ManageGame,

    /// <summary>Switching from one loadout to another.</summary>
    SwitchLoadout,

    /// <summary>Putting the game folder back to the way the store installed it.</summary>
    ResetGameFolder,

    /// <summary>Moving a folder to another deployment method.</summary>
    DeploymentMethodSwitch,

    /// <summary>Removing a game from the app.</summary>
    Unmanage,

    /// <summary>Deleting a loadout that is applied.</summary>
    DeleteLoadout,
}

/// <summary>
/// Keeps what started the current operation for the syncs it runs. A switch, a reset or a launch is several syncs in
/// a row, and each logs the same cause.
/// </summary>
/// <remarks>
/// Ambient for the same reason as <see cref="BackupOverflowAnswer"/>: the legs are separate calls through the loadout
/// manager and the synchronizer, and none of them is told why it runs. A scope that starts inside another one joins
/// it, so the outermost operation names the cause.
/// </remarks>
public static class SyncOrigin
{
    private static readonly AsyncLocal<Scope?> Active = new();

    /// <summary>What started the operation the caller is in, or null outside one.</summary>
    public static SyncTrigger? Current => Active.Value?.Trigger;

    /// <summary>Starts a scope for <paramref name="trigger"/>, or joins the one already open. Dispose it when the operation ends.</summary>
    public static IDisposable Begin(SyncTrigger trigger)
    {
        if (Active.Value is not null) return Joined.Instance;

        var scope = new Scope(trigger);
        Active.Value = scope;
        return scope;
    }

    /// <summary>The cause in plain words, to follow "started by".</summary>
    public static string Describe(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.ApplyButton => "the Apply button",
        SyncTrigger.Launch => "launching the game",
        SyncTrigger.AfterGameRun => "the game closing",
        SyncTrigger.ToolRun => "running a tool",
        SyncTrigger.CommandLine => "a command line command",
        SyncTrigger.ManageGame => "adding the game",
        SyncTrigger.SwitchLoadout => "switching loadouts",
        SyncTrigger.ResetGameFolder => "putting the game folder back",
        SyncTrigger.DeploymentMethodSwitch => "changing the deployment method",
        SyncTrigger.Unmanage => "removing the game from the app",
        SyncTrigger.DeleteLoadout => "deleting the loadout",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
    };

    /// <summary>
    /// Logs the start of a sync or a reset with what started it. A caller that never opened a scope is a gap in the
    /// log, so it is a warning that says so rather than a line that looks complete.
    /// </summary>
    internal static void LogStart(ILogger logger, string what)
    {
        if (Current is { } trigger)
            logger.LogInformation("Starting {What}, started by {Trigger}", what, Describe(trigger));
        else
            logger.LogWarning("Starting {What}, started with no recorded origin, so the log cannot say why", what);
    }

    private sealed class Scope(SyncTrigger trigger) : IDisposable
    {
        public SyncTrigger Trigger { get; } = trigger;

        public void Dispose() => Active.Value = null;
    }

    private sealed class Joined : IDisposable
    {
        public static readonly Joined Instance = new();

        public void Dispose() { }
    }
}
