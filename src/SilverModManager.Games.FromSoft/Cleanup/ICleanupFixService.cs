using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>
/// Asks the user to run <paramref name="command"/> in a terminal because the app could not. Returns true once the
/// user says it is done; <paramref name="isCleared"/> reads whether the file is free now, so the prompt can keep
/// watching.
/// </summary>
/// <param name="command">The shell-quoted command to show.</param>
/// <param name="why">Why the app could not run it, or null.</param>
/// <param name="isCleared">Whether the file is no longer immutable.</param>
public delegate Task<bool> ManualCommandPrompt(string command, string? why, Func<bool> isCleared, CancellationToken cancellationToken);

public enum CleanupFixStatus
{
    /// <summary>The steps ran, see <see cref="CleanupFixOutcome.Result"/> for what was done and any failure.</summary>
    Applied,

    /// <summary>The folder asks for different steps than the confirmed ones, see <see cref="CleanupFixOutcome.NewPlan"/>; nothing was applied.</summary>
    PlanChanged,

    /// <summary>The privileged step did not happen (dialog dismissed, command not run) or the game is running; nothing was changed.</summary>
    Cancelled,
}

public sealed record CleanupFixOutcome(CleanupFixStatus Status, CleanupResult? Result = null, CleanupPlan? NewPlan = null, string? Message = null);

/// <summary>A cleanup running as a job, so it shows up with the other jobs.</summary>
public sealed record CleanupFixJob(LoadoutId LoadoutId) : IJobDefinition<CleanupFixOutcome>;

/// <summary>What the Health Check Fix button drives.</summary>
public interface ICleanupFixService
{
    CleanupPlan Plan(Loadout.ReadOnly loadout);

    Task<CleanupFixOutcome> FixAsync(Loadout.ReadOnly loadout, CleanupPlan confirmed, ManualCommandPrompt askUserToRun, CancellationToken cancellationToken = default);
}
