using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.ToolRunner;

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

    public static List<string> Argv(IEnumerable<string> head, AbsolutePath exe, IReadOnlyList<string> args, AbsolutePath prefix)
    {
        var argv = new List<string>(head) { exe.ToString() };
        foreach (var arg in args) argv.Add(WindowsPathTranslator.Translate(arg, prefix.ToString()));
        return argv;
    }

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
