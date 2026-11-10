using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.CrashLogs;
using SilverModManager.Games.CreationEngine.CrashLogs.Rules;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Emitters;

/// <summary>
/// Reads the crash logger's reports from the last <see cref="Window"/>, groups the ones that crashed the same way,
/// and says which mods supply the files each crash touched. A known cause (<see cref="CrashCauseRules"/>) names
/// what to do; without one the group is reported with its owners and an unknown cause.
/// </summary>
/// <remarks>
/// A log appears on disk without any loadout change, so crashes show up when Health Check is opened or refreshed
/// after the game exits. The logs are read from disk, not from the sync tree: the logger's folder is the game's.
/// </remarks>
public sealed class CrashReportEmitter(ICreationEngineGame game, ILogger<CrashReportEmitter> logger) : ASyncTreeEmitter
{
    /// <summary>Logs older than this are ignored.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private const string ThisLoadout = "this loadout";
    private const string NoLooseFile = "no loose file in this loadout supplies it";
    private const string UnknownCause = "unknown";
    // The details template ends the fix with a full stop, so this note has none.
    private const string LaunchedSinceNote = ". The game has been launched since";
    private const string NoFix = "Nothing in the log points at one mod, so look at the mods listed below";

    private readonly ICrashLogSource _source = new ScriptExtenderCrashLogSource(game);

    // One cache per emitter: the manager can diagnose two loadouts of one game at once. The key changes when the
    // logger rewrites a file, and a null value is a file no format reads.
    private readonly ConcurrentDictionary<(AbsolutePath Path, DateTime LastWriteUtc, Size Size), CrashReport?> _parsed = new();

    /// <inheritdoc />
    protected override async IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (game.ScriptExtenderLogFolder is null) yield break;

        var oldest = DateTime.UtcNow - Window;
        var files = _source.Find(loadout.InstallationInstance).Where(f => f.LastWriteUtc >= oldest).ToList();
        ForgetLogsNotIn(files);

        var reports = new List<(CrashLogFile File, CrashReport Report)>();
        var unreadable = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ParseCached(file) is { } report) reports.Add((file, report));
            else unreadable.Add(file.Path.FileName.ToString());
        }
        if (reports.Count == 0) yield break;

        var owners = new CrashLogOwners(syncTree, loadout.Db, ModProvidedFiles.For(loadout), GameInstalledPaths.For(loadout.Db, loadout.LoadoutId));
        var index = LoadoutFileIndex.For(game, loadout);

        foreach (var group in CrashGroups.Of(reports))
        {
            var ownership = owners.Of(group.Representative);
            var verdict = CrashCauseRules.FirstMatch(group.Representative, ownership, game);
            if (WentAway(verdict, owners, index)) continue;

            var fit = LoadoutFit.Of(group.Representative, index);
            var launchedSince = LaunchedSince(loadout.InstallationInstance, group);
            yield return Render(group, ownership, verdict, unreadable, fit, launchedSince);
        }
    }

    /// <summary>Whether disabling the mod the verdict is about made its cause go away: its file is only in a disabled mod now.</summary>
    private static bool WentAway(CrashVerdict? verdict, CrashLogOwners owners, LoadoutFileIndex index) =>
        verdict?.SubjectFile is { } file && owners.OwnerOf(file) is null && index.DisabledPaths.Contains(file);

    /// <summary>
    /// Whether the script extender's loader has rewritten its launch log since the group's newest crash. The loader
    /// does that at every launch and the crash handler never touches it, so a newer launch log means the game
    /// started again after the crash.
    /// </summary>
    private bool LaunchedSince(GameInstallation installation, CrashGroup group)
    {
        if (game.ScriptExtenderLog is not { } launchLog) return false;
        try
        {
            var file = installation.Locations.ToAbsolutePath(launchLog);
            return file.FileExists && file.FileInfo.LastWriteTimeUtc > group.Newest.LastWriteUtc;
        }
        catch (IOException e)
        {
            logger.LogDebug(e, "Could not stat the script extender log");
            return false;
        }
    }

    private void ForgetLogsNotIn(IReadOnlyList<CrashLogFile> files)
    {
        var current = files.Select(f => (f.Path, f.LastWriteUtc, f.Size)).ToHashSet();
        foreach (var key in _parsed.Keys.Where(k => !current.Contains(k))) _parsed.TryRemove(key, out _);
    }

    private CrashReport? ParseCached(CrashLogFile file)
    {
        var key = (file.Path, file.LastWriteUtc, file.Size);
        if (_parsed.TryGetValue(key, out var cached)) return cached;

        string text;
        try
        {
            text = _source.ReadText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not cached: the file may be readable on the next refresh.
            logger.LogDebug(e, "Could not read crash log {Path}", file.Path);
            return null;
        }

        CrashReport? report = null;
        if (CrashLogFormats.Detect(text) is { } format)
        {
            report = format.Parse(text);
            logger.LogDebug("Parsed crash log {Path}", file.Path);
        }
        else
        {
            logger.LogDebug("No crash log format reads {Path}", file.Path);
        }
        return _parsed[key] = report;
    }

    private Diagnostic Render(CrashGroup group, CrashOwnership ownership, CrashVerdict? verdict, IReadOnlyList<string> unreadable, LoadoutFit? fit, bool launchedSince)
    {
        var report = group.Representative;
        var where = Where(report);
        var cause = verdict?.Cause ?? UnknownCause;
        var fix = verdict?.Fix ?? NoFix;
        var owners = new MarkdownFragment(Owners(ownership));
        var lastSeen = report.CrashTime ?? group.LastSeen.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // Owners of another loadout's log were resolved against this loadout's tree, so no mod is named and the card is a hint.
        if (fit is { IsAnotherLoadout: true } other)
        {
            var origin = $"probably another loadout of this game: {other.Absent} of the {other.Total} plugins it loaded are not in this one";
            Diagnostic labelled = verdict switch
            {
                { Startup: true } => Diagnostics.CreateStartupCrash(group.Count, cause, fix, owners, origin, lastSeen),
                _ when group.Count >= 2 => Diagnostics.CreateRepeatedCrash(group.Count, where, cause, fix, owners, origin, lastSeen),
                _ => Diagnostics.CreateOneOffCrash(where, cause, fix, owners, origin, lastSeen),
            };
            return labelled with { Severity = DiagnosticSeverity.Suggestion, MoreDetails = MoreDetails(group, ownership, unreadable) };
        }

        Diagnostic diagnostic = verdict switch
        {
            { Startup: true } => Diagnostics.CreateStartupCrash(group.Count, cause, fix + (launchedSince ? LaunchedSinceNote : ""), owners, ThisLoadout, lastSeen)
                with { Severity = launchedSince ? DiagnosticSeverity.Warning : DiagnosticSeverity.Critical },
            { NamedMod: { } mod } when group.Count >= 2 => Diagnostics.CreateRepeatedCrashNamingAMod(mod, group.Count, where, cause, fix, owners, ThisLoadout, lastSeen),
            _ when group.Count >= 2 => Diagnostics.CreateRepeatedCrash(group.Count, where, cause, fix, owners, ThisLoadout, lastSeen),
            _ => Diagnostics.CreateOneOffCrash(where, cause, fix, owners, ThisLoadout, lastSeen),
        };
        return diagnostic with { MoreDetails = MoreDetails(group, ownership, unreadable) };
    }

    private static string Where(CrashReport report)
    {
        var frame = report.Frames.FirstOrDefault(f => f.Kind == 'P')?.DisplayName ?? "an unknown function";
        return $"{report.ThreadRole ?? "running"} in {report.FaultModule ?? "an unknown module"} at {frame}";
    }

    private static string Owners(CrashOwnership ownership)
    {
        var lines = ownership.Assets.Select(o => Line(o, passedThrough: false))
            .Concat(ownership.Plugins.Select(o => Line(o, passedThrough: false)))
            .Concat(ownership.SkseDlls.Select(o => Line(o, passedThrough: true)))
            .ToList();
        return lines.Count == 0 ? $"- {MarkdownText.Inline("nothing the crash touched could be matched to a file")}" : string.Join('\n', lines);
    }

    private static string Line(OwnedPath file, bool passedThrough)
    {
        var path = MarkdownText.Code(file.Path.Path.ToString());
        var text = file switch
        {
            { Owner: { } owner } => $"{MarkdownText.Inline(owner.Name)}: {path}",
            { ViaPlugin: { } plugin } => $"{MarkdownText.Inline(plugin.Name)}: {path} ({NoLooseFile})",
            { IsGameFile: true } => $"{path} (a game file)",
            _ => $"{path} ({NoLooseFile})",
        };
        return passedThrough ? $"- {text} (the crash passed through it)" : $"- {text}";
    }

    private static string MoreDetails(CrashGroup group, CrashOwnership ownership, IReadOnlyList<string> unreadable)
    {
        var report = group.Representative;
        var text = new StringBuilder();
        text.AppendLine($"Log: {MarkdownText.Code(group.Newest.Path.FileName.ToString())}");
        if (report.LoggerVersion is { } logger) text.AppendLine().AppendLine($"Logger: {MarkdownText.Code(logger)}");

        Section(text, "Probable frames", report.Frames.Where(f => f.Kind == 'P')
            .Select(f => $"[{f.Index}] {MarkdownText.Code($"{f.Module}+{f.Offset}")} {MarkdownText.Inline(f.DisplayName)}"));
        Section(text, "Relevant objects", report.RelevantObjects
            .Select(o => $"{MarkdownText.Code(o.Type)} {MarkdownText.Inline(o.Name)} {(o.Parenthetical is { } p ? MarkdownText.Code(p) : "")}".TrimEnd()));
        Section(text, "Modified by", ownership.ModifiedByChains.Select(MarkdownText.Inline));
        Section(text, "Could not read", unreadable.Select(MarkdownText.Code));
        return text.ToString().TrimEnd();
    }

    private static void Section(StringBuilder text, string title, IEnumerable<string> items)
    {
        var lines = items.ToList();
        if (lines.Count == 0) return;
        text.AppendLine().AppendLine($"{title}:").AppendLine();
        foreach (var line in lines) text.AppendLine($"- {line}");
    }
}
