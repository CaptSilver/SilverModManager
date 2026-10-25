using System.Collections.Immutable;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>What a cleanup step asks for.</summary>
public enum CleanupStepKind
{
    /// <summary>
    /// A command that needs root, carried as <see cref="CleanupStep.Command"/>. Only <see cref="ElevatedCommandRunner"/>
    /// runs it, after the user's password prompt; the command line shows the command for the user to run.
    /// </summary>
    Privileged,

    /// <summary>Moves <see cref="CleanupStep.Source"/> over <see cref="CleanupStep.Target"/>, both inside the game folder.</summary>
    Move,

    /// <summary>Copies a file of the game folder into the loadout; <see cref="CleanupStep.Target"/> is its path inside the mods folder.</summary>
    Copy,

    /// <summary>Removes <see cref="CleanupStep.Source"/> from the game folder. A link is removed, never followed.</summary>
    Delete,

    /// <summary>Lists something the cleanup does not recognise and leaves alone.</summary>
    Review,

    /// <summary>Replaces Steam's launch options line <see cref="CleanupStep.Source"/> with <see cref="CleanupStep.Target"/>.</summary>
    ReplaceLaunchOption,

    /// <summary>Something for the user to know or do that the cleanup does not do itself.</summary>
    Advice,
}

/// <param name="Kind">What the step does.</param>
/// <param name="Description">The step in a sentence, for the list the user confirms.</param>
/// <param name="Source">What the step acts on; a path relative to the game folder except for <see cref="CleanupStepKind.ReplaceLaunchOption"/>.</param>
/// <param name="Target">Where the step puts it, see <see cref="CleanupStepKind"/>.</param>
/// <param name="BlockedReason">Set when the step cannot run yet and why.</param>
/// <param name="Command">The program and arguments of a <see cref="CleanupStepKind.Privileged"/> step.</param>
public sealed record CleanupStep(CleanupStepKind Kind, string Description, string? Source = null, string? Target = null, string? BlockedReason = null, PrivilegedCommand? Command = null)
{
    public bool IsBlocked => BlockedReason is not null;

    /// <summary><see cref="Source"/>, for a step whose kind always sets it.</summary>
    public string RequiredSource => Source ?? throw new InvalidOperationException($"The {Kind} step '{Description}' names nothing to act on.");

    /// <summary><see cref="Target"/>, for a step whose kind always sets it.</summary>
    public string RequiredTarget => Target ?? throw new InvalidOperationException($"The {Kind} step '{Description}' names nowhere to put it.");

    /// <summary><see cref="Command"/>, for a step whose kind always sets it.</summary>
    public PrivilegedCommand RequiredCommand => Command ?? throw new InvalidOperationException($"The {Kind} step '{Description}' carries no command.");

    /// <summary>Whether running the step changes anything.</summary>
    public bool Acts => Kind is CleanupStepKind.Move or CleanupStepKind.Copy or CleanupStepKind.Delete or CleanupStepKind.ReplaceLaunchOption;
}

/// <summary>The ordered steps that turn a game folder left by another mod manager back into a plain Steam install.</summary>
public sealed record CleanupPlan(ImmutableArray<CleanupStep> Steps)
{
    /// <summary>A pristine folder has nothing to do.</summary>
    public bool IsEmpty => Steps.IsDefaultOrEmpty;

    /// <summary>
    /// True while a privileged step has not run yet, or a step cannot run. Nothing is applied until the plan is
    /// rebuilt without any.
    /// </summary>
    public bool IsBlocked => Steps.Any(s => s.Kind == CleanupStepKind.Privileged || s.IsBlocked);

    /// <summary>Whether <paramref name="other"/> has the same steps in the same order.</summary>
    public bool SameSteps(CleanupPlan other) => Steps.SequenceEqual(other.Steps);
}
