using NexusMods.Paths;
using SilverModManager.Sdk;
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

    /// <summary>The variable Proton's <c>proton</c> script reads to decide whether to use its ARM64 Wine.</summary>
    public const string ProtonUseArm64Variable = "PROTON_USE_ARM64";

    /// <summary>
    /// Whether the Proton tree around this <c>proton</c> script carries an ARM64 build. The script itself checks
    /// for the <c>files/bin-arm64/</c> directory, not for any file inside it.
    /// </summary>
    public static bool HasArm64Build(AbsolutePath protonScript)
        => protonScript.Parent.Combine("files/bin-arm64").DirectoryExists();

    /// <summary>
    /// Null when the <c>proton</c> script would pick its ARM64 Wine on an aarch64 host; otherwise why it would
    /// not. The script takes the ARM64 path only when <c>PROTON_USE_ARM64</c> is unset or exactly <c>1</c>, so
    /// any other value is reported as the reason.
    /// </summary>
    public static string? ProtonArm64Requirement(AbsolutePath protonScript, string buildName, IEnvironmentReader environment, string pickAdvice)
    {
        if (!HasArm64Build(protonScript))
            return $"the Proton build {buildName} at '{protonScript.Parent}' has no ARM64 build (files/bin-arm64); {pickAdvice}";

        var value = environment.Get(ProtonUseArm64Variable) ?? "1";
        return value == "1"
            ? null
            : $"{ProtonUseArm64Variable} is set to '{value}', so Proton skips its ARM64 Wine; unset it";
    }

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
    /// A command's own start folder wins; otherwise an executable starts next to itself, a verb in the prefix.
    /// </summary>
    public static AbsolutePath WorkingDirectory(PrefixCommand command, ResolvedPrefix prefix) => command.WorkingDirectory ?? command.Exe?.Parent ?? prefix.Path;

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
