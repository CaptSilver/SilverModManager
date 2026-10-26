using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.PrefixSetup;

/// <summary>
/// What to set up and whether to touch the prefix.
/// </summary>
[PublicAPI]
public sealed record PrefixSetupRequest
{
    /// <summary>The components to bring into the prefix, in the order they are installed.</summary>
    public required IReadOnlyList<PrefixComponentId> Components { get; init; }

    /// <summary>False for a dry run, which only reports the plan.</summary>
    public bool Write { get; init; }

    /// <summary>
    /// The prefix path the user typed or confirmed. A write is refused unless it is the prefix the game
    /// resolves to, so a stale or mistaken confirmation never reaches another game's prefix.
    /// </summary>
    public string? ConfirmedPrefixPath { get; init; }

    /// <summary>How many times a failing step is tried before the run stops at it.</summary>
    public int Attempts { get; init; } = 2;
}

/// <summary>
/// What one step brings in and how, taken from the component table.
/// </summary>
/// <param name="Downloads">Files to fetch into the cache and verify.</param>
/// <param name="Actions">What to do with them, in order.</param>
/// <param name="Environment">Variables to run installers with.</param>
[PublicAPI]
public sealed record PrefixSetupStepPlan(
    ImmutableArray<PrefixDownload> Downloads,
    ImmutableArray<PrefixInstallStep> Actions,
    ImmutableDictionary<string, string> Environment)
{
    /// <summary>
    /// Whether the step runs an installer, which can write to the registry. Copying a DLL cannot.
    /// </summary>
    public bool TouchesRegistry => Actions.Any(a => a.Kind == PrefixInstallStepKind.RunInstaller);
}

/// <summary>
/// One unit of a prefix setup: how to tell it is done and what to do when it is not.
/// </summary>
/// <param name="Id">The component's id.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Detect">Checks the prefix for the component. Read-only.</param>
/// <param name="Plan">What installing it involves.</param>
[PublicAPI]
public sealed record PrefixSetupStep(PrefixComponentId Id, string Name, Func<PrefixComponentStatus> Detect, PrefixSetupStepPlan Plan);

/// <summary>
/// One step as shown in the plan preview.
/// </summary>
/// <param name="Id">The component's id.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Detected">What the prefix showed when the plan was made.</param>
/// <param name="Downloads">What would be fetched, with the hash each file must have.</param>
/// <param name="Actions">What would be done, one line each.</param>
/// <param name="WillInstall">False when the component is already installed and the step is skipped.</param>
[PublicAPI]
public sealed record PrefixSetupPlanEntry(
    PrefixComponentId Id,
    string Name,
    PrefixComponentStatus Detected,
    IReadOnlyList<PrefixDownload> Downloads,
    IReadOnlyList<string> Actions,
    bool WillInstall);

/// <summary>
/// What a setup would do to a prefix. A dry run returns only this.
/// </summary>
/// <param name="Prefix">The prefix that would be changed.</param>
/// <param name="Steps">One entry per requested component.</param>
/// <param name="BackupDirectory">Where the registry files would be copied first, or null when no step touches the registry.</param>
/// <param name="SharedNote">A warning that the prefix is shared with other uses of the game, or null.</param>
[PublicAPI]
public sealed record PrefixSetupPlan(
    ResolvedPrefix Prefix,
    IReadOnlyList<PrefixSetupPlanEntry> Steps,
    AbsolutePath? BackupDirectory,
    string? SharedNote);

/// <summary>How one step ended.</summary>
[PublicAPI]
public enum PrefixSetupStepStatus
{
    /// <summary>The prefix already had it, so nothing ran.</summary>
    AlreadyInstalled,

    /// <summary>The install ran and the prefix now shows the component.</summary>
    Installed,

    /// <summary>The install did not complete; see the error.</summary>
    Failed,

    /// <summary>An earlier step failed, so this one never started.</summary>
    NotRun,
}

/// <summary>
/// The outcome of one step.
/// </summary>
[PublicAPI]
public sealed record PrefixSetupStepResult(PrefixComponentId Id, string Name, PrefixSetupStepStatus Status, string? Error = null);

/// <summary>
/// The outcome of a setup run.
/// </summary>
/// <param name="Plan">The plan the run worked from.</param>
/// <param name="Written">Whether the prefix was allowed to change (a write rather than a dry run).</param>
/// <param name="Steps">One result per step of the plan; empty for a dry run.</param>
[PublicAPI]
public sealed record PrefixSetupResult(PrefixSetupPlan Plan, bool Written, IReadOnlyList<PrefixSetupStepResult> Steps)
{
    /// <summary>The step that failed, or null.</summary>
    public PrefixSetupStepResult? Failure => Steps.FirstOrDefault(s => s.Status == PrefixSetupStepStatus.Failed);
}

/// <summary>
/// The setup was refused before anything changed, for a reason the user can act on: the game is running,
/// the prefix does not exist yet, or the write was not confirmed.
/// </summary>
public sealed class PrefixSetupRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc/>
    public override string Title => "Prefix not changed";
}

/// <summary>
/// A setup stopped at a step that failed. The prefix keeps whatever the earlier steps installed.
/// </summary>
public sealed class PrefixSetupFailedException(string message) : UserFacingException(message)
{
    /// <inheritdoc/>
    public override string Title => "Prefix setup failed";
}
