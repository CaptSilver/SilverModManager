using NexusMods.Paths;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// What a run-game tool starts: the executable and the arguments it adds on top of whatever the caller passes.
/// </summary>
/// <param name="Exe">The executable to start.</param>
/// <param name="ExtraArgs">Arguments the tool always needs, appended after the caller's own arguments.</param>
public record LaunchSpec(AbsolutePath Exe, string[] ExtraArgs)
{
    /// <summary>
    /// True when the spec is the entire command: <see cref="Exe"/> followed by <see cref="ExtraArgs"/> is what
    /// gets started, and the command Steam expanded for <c>%command%</c> is read only for the app id. Without it
    /// the exe is put in the place of the game's own exe inside Steam's command.
    /// </summary>
    public bool ReplacesCommand { get; init; }
}
