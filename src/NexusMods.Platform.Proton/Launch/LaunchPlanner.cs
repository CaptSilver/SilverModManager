using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Sdk;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Turns the command Steam handed over into the one to start: the loadout's run tool decides which exe and
/// arguments the game needs, and the command is rewritten to match.
/// </summary>
public static class LaunchPlanner
{
    /// <summary>
    /// Rewrites <paramref name="command"/> for <paramref name="spec"/>. Throws with the substitution's text
    /// when the command can't be rewritten, because starting it unchanged would run the game without its mods.
    /// A spec that <see cref="LaunchSpec.ReplacesCommand"/> is the command itself and nothing of Steam's is kept.
    /// </summary>
    /// <param name="command">The command as Steam expanded <c>%command%</c>.</param>
    /// <param name="gameRoot">The installation's game folder.</param>
    /// <param name="primaryExe">The game's own executable.</param>
    /// <param name="spec">What the run tool wants started.</param>
    /// <param name="filesystemRoots">Host folders the deployment backends need visible inside the Steam container.</param>
    /// <param name="wineDllOverrides">The overrides the game needs, set in the environment the command starts with.</param>
    public static LaunchPlan Compose(IReadOnlyList<string> command, AbsolutePath gameRoot, AbsolutePath primaryExe, LaunchSpec spec, IReadOnlyList<string> filesystemRoots, IReadOnlyList<WineDllOverride> wineDllOverrides)
    {
        if (spec.ReplacesCommand)
        {
            // The run tool owns the whole command (a launcher that builds its own Proton chain), so Steam's chain is
            // kept only for the app id, which the launcher cannot otherwise know.
            var replaced = new ExeSubstitution.Result([spec.Exe.GetFullPath(), ..spec.ExtraArgs], null, ExeSubstitution.ExtractAppId(command));
            return new LaunchPlan(new GameProcessSpec(replaced.Argv, FilesystemRoots: filesystemRoots, WineDllOverrides: wineDllOverrides), spec, replaced);
        }

        // Arguments the game needs count like a loader: a command with no exe to put them behind would start the game without them.
        var loaderResolved = !spec.Exe.Equals(primaryExe) || spec.ExtraArgs.Length > 0;
        var substitution = ExeSubstitution.Apply(command, gameRoot, spec, loaderResolved);
        if (substitution.IsRefused) throw new InvalidOperationException(substitution.Refusal);

        return new LaunchPlan(new GameProcessSpec(substitution.Argv, FilesystemRoots: filesystemRoots, WineDllOverrides: wineDllOverrides), spec, substitution);
    }
}
