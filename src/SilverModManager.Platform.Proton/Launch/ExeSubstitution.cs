using NexusMods.Abstractions.Games;
using NexusMods.Paths;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Rewrites the command Steam hands to <c>silvermod run -- %command%</c> so it starts the exe the loadout's
/// run tool resolved.
/// </summary>
public static class ExeSubstitution
{
    private const string ProtonVerb = "waitforexitandrun";
    private const string AppIdPrefix = "AppId=";

    /// <summary>
    /// The outcome of a substitution.
    /// </summary>
    /// <param name="Argv">The command to run. Empty when the substitution was refused.</param>
    /// <param name="Refusal">Why the command cannot be rewritten, naming the argv, or null.</param>
    /// <param name="AppId">The Steam app id found behind the wrapper prefix, or null.</param>
    public sealed record Result(IReadOnlyList<string> Argv, string? Refusal, string? AppId)
    {
        /// <summary>
        /// Whether the substitution was refused.
        /// </summary>
        public bool IsRefused => Refusal is not null;
    }

    /// <summary>
    /// Replaces the exe slot of the command with the spec's exe and inserts the spec's extra arguments directly
    /// after it, ahead of the arguments the slot already had. A slot that is already the spec's exe and a spec
    /// with no extra arguments leave the command as it was.
    /// </summary>
    /// <param name="argv">The command as Steam expanded <c>%command%</c>.</param>
    /// <param name="gameRoot">The installation's game folder. Only an exe under it can be the slot.</param>
    /// <param name="spec">What the run tool wants started.</param>
    /// <param name="loaderResolved">
    /// Whether the run tool resolved something other than the game's own exe (a script extender loader, or
    /// arguments the game needs). With no slot to put it in, that is a refusal rather than a plain launch,
    /// because running the command unchanged would start the game without the mods.
    /// </param>
    public static Result Apply(IReadOnlyList<string> argv, AbsolutePath gameRoot, LaunchSpec spec, bool loaderResolved)
    {
        var appId = ExtractAppId(argv);
        var root = gameRoot.GetFullPath();
        var slot = FindSlot(argv, root);

        if (slot < 0)
        {
            if (!loaderResolved) return new Result(argv, null, appId);
            return new Result([],
                "The launch command has no .exe under the game folder to replace, so the loadout's " +
                $"executable cannot be put in its place: {string.Join(' ', argv)}", appId);
        }

        // Steam already starting the right exe still needs the tool's arguments, or the game runs without its mods.
        var exe = spec.Exe.GetFullPath();
        var sameExe = string.Equals(Path.GetFullPath(argv[slot]), exe, StringComparison.Ordinal);
        if (sameExe && spec.ExtraArgs.Length == 0) return new Result(argv, null, appId);

        var result = new List<string>(argv.Count + spec.ExtraArgs.Length);
        result.AddRange(argv.Take(slot));
        result.Add(exe);
        result.AddRange(spec.ExtraArgs);
        result.AddRange(argv.Skip(slot + 1));
        return new Result(result, null, appId);
    }

    /// <summary>
    /// Finds the app id in the <c>reaper SteamLaunch AppId=N</c> part of the command, wherever the wrapper
    /// prefix put it.
    /// </summary>
    public static string? ExtractAppId(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            if (argv[i - 1] != "SteamLaunch") continue;
            var arg = argv[i];
            if (!arg.StartsWith(AppIdPrefix, StringComparison.Ordinal)) continue;

            var id = arg[AppIdPrefix.Length..];
            if (id.Length > 0 && id.All(char.IsAsciiDigit)) return id;
        }

        return null;
    }

    /// <summary>
    /// The argv index of the exe to replace, or -1. Steam's command has Proton's verb right before it; a
    /// command without the verb (a native or terminal one) uses the first exe under the game folder.
    /// </summary>
    private static int FindSlot(IReadOnlyList<string> argv, string root)
    {
        var verb = IndexOfProtonVerb(argv);
        if (verb >= 0) return verb + 1 < argv.Count && IsGameExe(argv[verb + 1], root) ? verb + 1 : -1;

        for (var i = 0; i < argv.Count; i++)
        {
            if (IsGameExe(argv[i], root)) return i;
        }

        return -1;
    }

    private static int IndexOfProtonVerb(IReadOnlyList<string> argv)
    {
        // The verb is also passed as --verb=waitforexitandrun, which is a different token.
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == ProtonVerb) return i;
        }

        return -1;
    }

    private static bool IsGameExe(string arg, string root)
    {
        if (!arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(arg)) return false;

        var full = Path.GetFullPath(arg);
        var prefix = root.EndsWith('/') ? root : root + "/";
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }
}
