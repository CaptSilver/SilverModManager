using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// The arguments of <c>silvermod run &lt;loadout&gt; [--game &lt;appid|name&gt;] [--secrets-file &lt;path&gt;] [--dry-run] -- &lt;command&gt;</c>.
/// </summary>
/// <param name="LoadoutName">The loadout to run, as typed.</param>
/// <param name="Game">The <c>--game</c> value (a Steam app id or a game name), or null.</param>
/// <param name="Command">Everything after the first <c>--</c>, untouched.</param>
/// <param name="DryRun">Print what would be launched and stop, without locking, applying or starting anything.</param>
/// <param name="SecretsFile">The <c>--secrets-file</c> value, which supplies credentials for this process instead of the secret store, or null.</param>
public sealed record LaunchArgs(string LoadoutName, string? Game, IReadOnlyList<string> Command, bool DryRun = false, AbsolutePath? SecretsFile = null)
{
    /// <summary>
    /// The one-line usage text shown with every usage error and in <c>--help</c>.
    /// </summary>
    public const string Synopsis = "usage: silvermod run <loadout> [--game <appid|name>] [--secrets-file <path>] [--dry-run] -- <command> [args...]";

    /// <summary>
    /// The text <c>run --help</c> prints: the synopsis and one line per argument.
    /// </summary>
    public const string HelpText = Synopsis + """


          <loadout>       the loadout to run, by name
          --game <x>      the Steam app id or game name, when the loadout name alone is ambiguous
          --secrets-file <path>
                          read credentials from this NAME=value file instead of the secret store
                          (SILVERMM_SECRETS_FILE does the same for every process; the option wins)
          --dry-run       print what would be launched, without locking, applying or starting anything
          --              ends the options; everything after it is the command, passed through untouched

        Exits 125 when the launch cannot start; the game's own exit code otherwise.
        """;

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
        string? secretsFileValue = null;
        var dryRun = false;
        for (var i = 0; i < separator; i++)
        {
            var arg = args[i];
            string? value = null;
            string option;
            if (arg == game || arg == SecretsFileOption.Name)
            {
                option = arg;
                if (i + 1 >= separator)
                {
                    error = $"{option} needs a value. {Synopsis}";
                    return false;
                }
                value = args[++i];
            }
            else if (arg.StartsWith(game + "=", StringComparison.Ordinal))
            {
                option = game;
                value = arg[(game.Length + 1)..];
            }
            else if (arg.StartsWith(SecretsFileOption.Name + "=", StringComparison.Ordinal))
            {
                option = SecretsFileOption.Name;
                value = arg[(SecretsFileOption.Name.Length + 1)..];
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
                error = $"{option} needs a value. {Synopsis}";
                return false;
            }

            if ((option == game ? gameValue : secretsFileValue) is not null)
            {
                error = $"{option} given more than once. {Synopsis}";
                return false;
            }

            if (option == game) gameValue = value;
            else secretsFileValue = value;
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

        parsed = new LaunchArgs(name, gameValue, args.Skip(separator + 1).ToArray(), dryRun, secretsFileValue is null ? null : SecretsFileOption.ToPath(secretsFileValue));
        error = null;
        return true;
    }
}
