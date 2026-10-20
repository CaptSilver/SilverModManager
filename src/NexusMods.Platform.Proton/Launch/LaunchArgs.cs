using System.Diagnostics.CodeAnalysis;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// The arguments of <c>silvermod run &lt;loadout&gt; [--game &lt;appid|name&gt;] [--dry-run] -- &lt;command&gt;</c>.
/// </summary>
/// <param name="LoadoutName">The loadout to run, as typed.</param>
/// <param name="Game">The <c>--game</c> value (a Steam app id or a game name), or null.</param>
/// <param name="Command">Everything after the first <c>--</c>, untouched.</param>
/// <param name="DryRun">Print what would be launched and stop, without locking, applying or starting anything.</param>
public sealed record LaunchArgs(string LoadoutName, string? Game, IReadOnlyList<string> Command, bool DryRun = false)
{
    /// <summary>
    /// The one-line usage text shown with every usage error and in <c>--help</c>.
    /// </summary>
    public const string Synopsis = "usage: silvermod run <loadout> [--game <appid|name>] [--dry-run] -- <command> [args...]";

    /// <summary>
    /// Parses the arguments that follow <c>run</c>. Only what comes before the first <c>--</c> is interpreted:
    /// the command after it can contain anything, including another <c>--</c> or a <c>--game</c> of its own.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out LaunchArgs? parsed, [NotNullWhen(false)] out string? error)
    {
        const string game = "--game";
        const string dryRunFlag = "--dry-run";
        parsed = null;

        var separator = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != "--") continue;
            separator = i;
            break;
        }

        if (separator < 0)
        {
            error = $"Missing '--' before the command to run. {Synopsis}";
            return false;
        }

        string? name = null;
        string? gameValue = null;
        var dryRun = false;
        for (var i = 0; i < separator; i++)
        {
            var arg = args[i];
            string? value = null;
            if (arg == game)
            {
                if (i + 1 >= separator)
                {
                    error = $"{game} needs a value. {Synopsis}";
                    return false;
                }
                value = args[++i];
            }
            else if (arg.StartsWith(game + "=", StringComparison.Ordinal))
            {
                value = arg[(game.Length + 1)..];
            }
            else if (arg == dryRunFlag)
            {
                if (dryRun)
                {
                    error = $"{dryRunFlag} given more than once. {Synopsis}";
                    return false;
                }
                dryRun = true;
                continue;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unknown option '{arg}'. {Synopsis}";
                return false;
            }
            else
            {
                if (name is not null)
                {
                    error = $"Unexpected argument '{arg}' after the loadout name '{name}'. {Synopsis}";
                    return false;
                }
                name = arg;
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{game} needs a value. {Synopsis}";
                return false;
            }

            if (gameValue is not null)
            {
                error = $"{game} given more than once. {Synopsis}";
                return false;
            }
            gameValue = value;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"Missing the loadout name. {Synopsis}";
            return false;
        }

        if (separator == args.Count - 1)
        {
            error = $"Nothing to run after '--'. {Synopsis}";
            return false;
        }

        parsed = new LaunchArgs(name, gameValue, args.Skip(separator + 1).ToArray(), dryRun);
        error = null;
        return true;
    }
}
