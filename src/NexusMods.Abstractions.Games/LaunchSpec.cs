using NexusMods.Paths;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// What a run-game tool starts: the executable and the arguments it adds on top of whatever the caller passes.
/// </summary>
/// <param name="Exe">The executable to start.</param>
/// <param name="ExtraArgs">Arguments the tool always needs, appended after the caller's own arguments.</param>
public record LaunchSpec(AbsolutePath Exe, string[] ExtraArgs);
