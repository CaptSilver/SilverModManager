using System.Text.RegularExpressions;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;

/// <summary>A plugin RED4ext reported loading, with the version it announced.</summary>
internal sealed record RedPlugin(string Name, string Version);

/// <summary>
/// What one RED4ext log (<c>red4ext/logs/red4ext-&lt;timestamp&gt;.log</c>, a new file per launch) says about the
/// game it ran against and the plugins it loaded.
/// </summary>
internal sealed partial record Red4ExtLog(string? FileVersion, IReadOnlyList<RedPlugin> Plugins)
{
    [GeneratedRegex(@"\[RED4ext\] File version: (?<version>\d+(\.\d+)+)\s*$")]
    private static partial Regex FileVersionLine();

    // The plugin's own name can hold spaces and brackets, so it is everything before " (version: ".
    [GeneratedRegex(@"\[RED4ext\] (?<name>.+?) \(version: (?<version>[^,)]+), .*\) has been loaded\s*$")]
    private static partial Regex LoadedLine();

    public static Red4ExtLog Parse(IEnumerable<string> lines)
    {
        string? fileVersion = null;
        var plugins = new List<RedPlugin>();
        foreach (var line in lines)
        {
            if (fileVersion is null && FileVersionLine().Match(line) is { Success: true } version)
                fileVersion = version.Groups["version"].Value;
            else if (LoadedLine().Match(line) is { Success: true } plugin)
                plugins.Add(new RedPlugin(plugin.Groups["name"].Value, plugin.Groups["version"].Value));
        }
        return new Red4ExtLog(fileVersion, plugins);
    }
}

/// <summary>
/// Cyber Engine Tweaks appends every launch to one <c>cyber_engine_tweaks.log</c>, so the game version it saw
/// last is the last <c>Game version</c> line, not the first.
/// </summary>
internal static partial class CetLog
{
    [GeneratedRegex(@"\] Game version (?<version>\d+(\.\d+)+)\s*$")]
    private static partial Regex GameVersionLine();

    public static string? LastGameVersion(IEnumerable<string> lines)
    {
        string? last = null;
        foreach (var line in lines)
        {
            if (GameVersionLine().Match(line) is { Success: true } match)
                last = match.Groups["version"].Value;
        }
        return last;
    }
}
