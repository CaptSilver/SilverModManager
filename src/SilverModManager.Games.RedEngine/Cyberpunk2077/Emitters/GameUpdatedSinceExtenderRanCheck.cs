using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;

/// <summary>
/// Warns when Cyberpunk's executable is not the build RED4ext or Cyber Engine Tweaks last logged. Both hook
/// the game by address (CET refuses any build it wasn't made for), so a game updated since their last launch
/// leaves them, and the mods that load through them, broken until each gets a matching release. The logs are
/// read, so this works before the first launch after the update, which is when the warning is useful. It needs
/// one earlier launch with an extender, and clears itself when the next launch writes the new version.
/// </summary>
/// <remarks>
/// Versions are compared for inequality only: the game's own numbering is not ordered the way
/// <see cref="Version"/> orders it (2.5 sorts below 2.31).
/// </remarks>
public class GameUpdatedSinceExtenderRanCheck(ILogger<GameUpdatedSinceExtenderRanCheck> logger) : IGameUpdateCheck
{
    private const string Red4ExtName = "RED4ext";
    private const string CetName = "Cyber Engine Tweaks";

    private static readonly RelativePath ExePath = "bin/x64/Cyberpunk2077.exe";
    private static readonly RelativePath Red4ExtLogs = "red4ext/logs";
    private static readonly RelativePath CetLogPath = "bin/x64/plugins/cyber_engine_tweaks/cyber_engine_tweaks.log";

    public static readonly NamedLink Red4ExtReleases = new("RED4ext releases", new Uri("https://github.com/WopsS/RED4ext/releases"));
    public static readonly NamedLink CetReleases = new("Cyber Engine Tweaks releases", new Uri("https://github.com/maximegmd/CyberEngineTweaks/releases"));

    public IEnumerable<Diagnostic> Check(GameUpdateContext context)
    {
        var installation = context.Installation;
        if (installation.Game.GameId != Cyberpunk2077Game.GameId) yield break;

        var gameFolder = installation.Locations[LocationId.Game].Path;
        if (!TryReadExeVersion(gameFolder / ExePath, out var exeVersion)) yield break;

        var red4Ext = ReadRed4ExtLog(gameFolder / Red4ExtLogs);
        var cet = ReadCetVersion(gameFolder / CetLogPath);

        var stale = new List<(string Extender, string Logged)>();
        if (red4Ext?.FileVersion is { } red4ExtVersion && red4ExtVersion != exeVersion) stale.Add((Red4ExtName, red4ExtVersion));
        if (cet is not null && cet != exeVersion) stale.Add((CetName, cet));
        if (stale.Count == 0) yield break;

        yield return Diagnostics.CreateGameUpdatedSinceExtenderRan(
            Game: installation.Game.DisplayName,
            Extender: string.Join(" and ", stale.Select(s => s.Extender)),
            GameVersion: exeVersion,
            LoggedVersion: LoggedVersions(stale),
            Plugins: stale.Any(s => s.Extender == Red4ExtName) && red4Ext is { Plugins.Count: > 0 } ? PluginList(red4Ext.Plugins) : "",
            Red4ExtReleases: Red4ExtReleases,
            CetReleases: CetReleases
        );
    }

    /// <summary>The old version once when the extenders agree, or each with its extender when they last ran on different builds.</summary>
    private static string LoggedVersions(IReadOnlyList<(string Extender, string Logged)> stale)
        => stale.Select(s => s.Logged).Distinct().Count() == 1
            ? stale[0].Logged
            : string.Join(" and ", stale.Select(s => $"{s.Logged} ({s.Extender})"));

    private static string PluginList(IReadOnlyList<RedPlugin> plugins)
    {
        var text = new StringBuilder("RED4ext loaded these plugins last time:\n\n");
        foreach (var plugin in plugins) text.Append("- ").Append(plugin.Name).Append(' ').Append(plugin.Version).Append('\n');
        return text.ToString();
    }

    private bool TryReadExeVersion(AbsolutePath exe, out string version)
    {
        version = "";
        try
        {
            using var stream = exe.Read();
            if (PeFileVersion.Read(stream) is { } read)
            {
                version = read;
                return true;
            }
            logger.LogTrace("`{Exe}` carries no version resource; skipping the extender version check", exe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogTrace(exception, "Could not read `{Exe}`; skipping the extender version check", exe);
        }
        return false;
    }

    /// <summary>The newest RED4ext log: the files are named by launch time, so the last name is the latest launch.</summary>
    private Red4ExtLog? ReadRed4ExtLog(AbsolutePath folder)
    {
        try
        {
            if (!folder.DirectoryExists()) return null;
            var newest = folder.EnumerateFiles("red4ext-*.log", recursive: false).OrderBy(f => f.FileName, StringComparer.Ordinal).LastOrDefault();
            return newest == default ? null : Red4ExtLog.Parse(ReadLines(newest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogTrace(exception, "Could not read the RED4ext logs in `{Folder}`", folder);
            return null;
        }
    }

    private string? ReadCetVersion(AbsolutePath log)
    {
        try
        {
            return log.FileExists ? CetLog.LastGameVersion(ReadLines(log)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogTrace(exception, "Could not read `{Log}`", log);
            return null;
        }
    }

    /// <summary>The game may still be writing the log, so it is opened shared.</summary>
    private static IEnumerable<string> ReadLines(AbsolutePath file)
    {
        using var stream = new FileStream(file.GetFullPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
