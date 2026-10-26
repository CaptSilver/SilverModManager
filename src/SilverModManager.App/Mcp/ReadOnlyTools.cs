using System.Text;
using Microsoft.Extensions.Logging;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;

namespace SilverModManager.App.Mcp;

/// <summary>The tools that only read. A read-only server offers just these.</summary>
public static class ReadOnlyTools
{
    private const int DefaultLogLimit = 50;
    private const int MaxLogLimit = 500;

    public static IReadOnlyList<McpTool> Create(McpToolContext context) =>
    [
        AppStatus(context),
        AppLogErrors(context),
        VerbTool.Create(context, "loadouts_list",
            "Lists the loadouts with their id, name, game, version, store and item count.",
            readOnly: true, ["loadouts", "list"], takesLoadout: false, context.Limits.ReadTimeout),
        VerbTool.Create(context, "loadout_merge_report",
            "Reports the files the app merges for a FromSoft loadout, which mods feed each one, and whether an apply would be refused, as JSON. " +
            "Find the loadout with loadouts_list.",
            readOnly: true, ["fromsoft", "merge", "list"], takesLoadout: true, context.Limits.SlowReadTimeout, flags: ["--json"], reportsExitOne: true),
        VerbTool.Create(context, "loadout_vanilla_list",
            "Lists the vanilla regulations the app keeps for a FromSoft loadout's game and where each came from. Find the loadout with loadouts_list.",
            readOnly: true, ["fromsoft", "vanilla", "list"], takesLoadout: true, context.Limits.SlowReadTimeout),
        VerbTool.Create(context, "loadout_heal_plan",
            "Shows the steps that would bring a FromSoft loadout's merged regulation onto the installed game build, without changing anything. " +
            "Find the loadout with loadouts_list.",
            readOnly: true, ["fromsoft", "heal"], takesLoadout: true, context.Limits.SlowReadTimeout, flags: ["--dry-run"], reportsExitOne: true),
        VerbTool.Create(context, "games_list",
            "Lists, as JSON, the games found on this machine with their store and install folder.",
            readOnly: true, ["games", "list"], takesLoadout: false, context.Limits.ReadTimeout, flags: ["--json"]),
        VerbTool.Create(context, "loadout_health_check",
            "Runs the Health Check on a loadout again and returns, as JSON, the problems and suggestions it finds. " +
            "Find the loadout with loadouts_list.",
            readOnly: true, ["loadout", "diagnostics"], takesLoadout: true, context.Limits.SlowReadTimeout, flags: ["--refresh", "--json"]),
        VerbTool.Create(context, "doctor",
            "Reports, as JSON, how each managed game would launch: prefix, Proton, tool runner, Steam launch options and launch lock.",
            readOnly: true, ["doctor"], takesLoadout: false, context.Limits.ReadTimeout, flags: ["--json"], reportsExitOne: true),
    ];

    /// <summary>
    /// The versions of this binary and of the running app (they differ after an update), the app's uptime, and the
    /// launch locks. The locks are read from files, so they are reported even when the app is down.
    /// </summary>
    private static McpTool AppStatus(McpToolContext context) => new(
        "app_status",
        "Reports whether SilverModManager is running, which version it is, and which game launches hold a lock. Works when the app is closed.",
        [], ReadOnly: true,
        async (_, token) =>
        {
            var call = new VerbCall(context.Limits.ReadTimeout, ReportsExitOne: false, context.Limits.MaxOutputChars);
            var text = new StringBuilder();

            var commit = ApplicationConstants.CommitHash is { } hash ? $" ({hash})" : "";
            text.Append($"This binary: {ApplicationConstants.ProductName} {ApplicationConstants.Version}{commit}\n");

            // Every build answers heartbeat, so it decides whether the app is up; version only exists in newer ones.
            var heartbeat = await context.Verbs.RunAsync(["heartbeat"], call, token);
            if (heartbeat.IsError)
            {
                text.Append($"Running app: {heartbeat.Text.Trim()}\n");
            }
            else
            {
                // An older build answers an unknown verb with its whole usage text, which says nothing useful here.
                var version = await context.Verbs.RunAsync(["version"], call, token);
                text.Append($"Running app: {(version.IsError ? "a build too old to report its version" : version.Text.Trim())}\n");
                text.Append(heartbeat.Text.Trim()).Append('\n');
            }

            // Writers, not the console: stdout carries the protocol.
            using var output = new StringWriter();
            using var error = new StringWriter();
            StatusCommand.Run([], context.RuntimeDir, context.MountInfoPath, output, error);
            text.Append("\nLaunch status:\n").Append(output.ToString()).Append(error.ToString());

            return new ToolResult(VerbResults.Cap(text.ToString(), context.Limits.MaxOutputChars), false);
        });

    private static McpTool AppLogErrors(McpToolContext context) => new(
        "app_log_errors",
        "Reads the warnings and errors from the running app's log file, newest last, with stack traces. " +
        "The time filter is approximate: the log stamps entries with the time since the app started.",
        [
            new ToolParam("minLevel", "The least severe level to include: warn (the default), error or fatal. info, debug and trace also work but are noisy."),
            new ToolParam("limit", $"The most entries to return, newest kept. Default {DefaultLogLimit}.", Type: "integer"),
            new ToolParam("sinceMinutes", "Only entries from the last this many minutes.", Type: "integer"),
        ],
        ReadOnly: true,
        (args, _) =>
        {
            if (context.MainLogPath is not { } path) return Task.FromResult(new ToolResult("Where the app writes its log is not known.", true));

            LogLevel level;
            switch (args.String("minLevel")?.ToLowerInvariant())
            {
                case null or "warn" or "warning": level = LogLevel.Warning; break;
                case "error": level = LogLevel.Error; break;
                case "fatal" or "critical": level = LogLevel.Critical; break;
                case "info": level = LogLevel.Information; break;
                case "debug": level = LogLevel.Debug; break;
                case "trace": level = LogLevel.Trace; break;
                case var other: return Task.FromResult(new ToolResult($"Unknown level '{other}'. Use warn, error, fatal, info, debug or trace.", true));
            }

            var limit = Math.Clamp(args.Int("limit") ?? DefaultLogLimit, 1, MaxLogLimit);
            TimeSpan? since = args.Int("sinceMinutes") is { } minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

            return Task.Run(() =>
            {
                var text = LogReader.Read(path, level, limit, since, DateTime.Now);
                return new ToolResult(VerbResults.Cap(text, context.Limits.MaxOutputChars), false);
            });
        });
}
