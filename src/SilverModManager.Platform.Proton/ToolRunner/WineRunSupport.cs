using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// The parts of building a run that every non-Steam strategy shares.
/// </summary>
internal static class WineRunSupport
{
    /// <summary>
    /// Whether a launcher's runner binary is Proton's <c>proton</c> script, which takes <c>run</c> and a
    /// compat data directory, rather than a <c>wine</c> executable.
    /// </summary>
    public static bool IsProtonScript(AbsolutePath runnerBinary) => runnerBinary.FileName == "proton";

    public static string NoRunner(GameInstallation installation)
        => $"{installation.Game.DisplayName} runs through Heroic, but Heroic's settings name no runner for it.";

    public static List<string> Argv(IEnumerable<string> head, PrefixCommand command, AbsolutePath prefix)
    {
        var argv = new List<string>(head) { command.Program };
        argv.AddRange(Arguments(command, prefix));
        return argv;
    }

    /// <summary>
    /// The command's arguments as the program gets them. An executable's host paths become Windows paths; a
    /// verb's arguments are left alone, since its <c>/i</c>-style switches would be taken for host paths.
    /// </summary>
    public static IEnumerable<string> Arguments(PrefixCommand command, AbsolutePath prefix)
        => command.IsVerb ? command.Args : command.Args.Select(arg => WindowsPathTranslator.Translate(arg, prefix.ToString()));

    /// <summary>
    /// An executable starts next to itself, a verb in the prefix.
    /// </summary>
    public static AbsolutePath WorkingDirectory(PrefixCommand command, ResolvedPrefix prefix) => command.Exe?.Parent ?? prefix.Path;

    /// <summary>
    /// Error lines on every channel and nothing else. Proton sets -all for game performance, which a tool
    /// run does not need, and -all hides the loader's "Library X not found" lines that say why a tool
    /// stopped. Wine's parser starts from err+fixme and only "all" entries change that default, so
    /// "err+all" alone would keep the fixme spam; the leading -all clears it. The caller's variables still win.
    /// </summary>
    public const string ToolRunWineDebug = "-all,err+all";

    /// <summary>
    /// Sets the defaults every strategy that runs a <c>proton</c> script shares; call it before
    /// <see cref="Environment"/> so the caller's variables override them.
    /// </summary>
    public static void ProtonDefaults(Dictionary<string, string> environment) => environment.TryAdd("WINEDEBUG", ToolRunWineDebug);

    /// <summary>
    /// <paramref name="defaults"/> overlaid with the caller's variables, which win on a clash.
    /// </summary>
    public static Dictionary<string, string> Environment(Dictionary<string, string> defaults, IReadOnlyDictionary<string, string>? caller)
    {
        if (caller is not null)
        {
            foreach (var (name, value) in caller) defaults[name] = value;
        }

        return defaults;
    }
}
