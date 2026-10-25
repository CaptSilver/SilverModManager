using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Steam;

namespace SilverModManager.Platform.Proton.Steam;

/// <summary>
/// Reports a Steam game whose launch options don't run the loadout, which is what makes Steam's own Play
/// button (and the app's Play, when it hands the game to Steam) skip what the app adds at launch.
/// </summary>
/// <remarks>
/// Registered for every game; it only reports a Steam install whose <c>localconfig.vdf</c> can be located, and
/// only reads that file. A file it can't read says nothing about the game, so it is not a finding.
/// A line that isn't ours is only worth reporting when the launch matters: the loadout's files exist only
/// through the app (<see cref="LoadoutLaunchNeeds"/>), or the game needs an executable, arguments or a launcher
/// skip that Steam's own start leaves out. Another manager's line is always reported, since it may rewrite the
/// game folder.
/// </remarks>
public sealed class SteamLaunchOptionEmitter(IToolManager tools, IConnection connection, IDeploymentPlanStore plans, ISteamLaunchOptionWriter launchOptions, ILogger<SteamLaunchOptionEmitter> logger) : ILoadoutDiagnosticEmitter
{
    /// <param name="OtherManager">The launcher the line starts instead of the app, or null when the line is simply not ours.</param>
    /// <param name="CurrentLine">The launch options Steam has stored for the game, or null when there are none.</param>
    internal sealed record Finding(string? OtherManager, string? CurrentLine = null);

    /// <inheritdoc />
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseAsync(loadout, cancellationToken);

    private async IAsyncEnumerable<Diagnostic> DiagnoseAsync(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        if (Classify(installation, loadout.Name) is not { } finding) yield break;

        var plan = plans.Load(connection.Db, loadout.InstallationId);
        var diagnostic = Evaluate(
            installation, loadout.Name, finding, plan,
            WineDllOverrideRequirements.For(installation.Game, loadout),
            GameLauncherSkip.For(installation.Game),
            await ResolveLaunchSpec(tools.GetTools(loadout).OfType<IRunGameTool>().FirstOrDefault(), loadout, loadout.Name, logger, cancellationToken),
            installation.Locations.ToAbsolutePath(installation.Game.GetPrimaryFile(installation)),
            launchOptions.ComposeLine(loadout));
        if (diagnostic is not null) yield return diagnostic;
    }

    /// <summary>
    /// What the run tool would start, or null when it can't say. A tool refuses (<see cref="InvalidOperationException"/>)
    /// while something it needs is missing, such as the mod loader or bwrap; that is a state the user sees in its
    /// own diagnostic, so here it is only noted. The suggestion still lists what the game declares.
    /// </summary>
    internal static async ValueTask<LaunchSpec?> ResolveLaunchSpec(IRunGameTool? runTool, Loadout.ReadOnly loadout, string loadoutName, ILogger logger, CancellationToken cancellationToken)
    {
        if (runTool is null) return null;

        try
        {
            return await runTool.GetLaunchSpec(loadout, cancellationToken);
        }
        catch (InvalidOperationException e)
        {
            logger.LogInformation("The launch of {Loadout} can't be resolved yet, so the Steam launch options suggestion leaves out the run tool's executable and arguments: {Reason}", loadoutName, e.Message);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not resolve the launch of {Loadout} for the Steam launch options diagnostic", loadoutName);
            return null;
        }
    }

    /// <summary>
    /// What the game's Steam launch options say about the loadout, or null when there is nothing to report.
    /// </summary>
    internal static Finding? Classify(GameInstallation installation, string loadoutName)
    {
        if (SteamLaunchOptionWriter.Locate(installation) is not { } game) return null;

        var read = LocalConfigLaunchOptions.Read(game.Config, game.AppId);
        if (!read.FileReadable) return null;
        if (read.Line is null) return new Finding(null);

        var parsed = SteamLaunchOptionLine.Parse(read.Line);
        if (parsed.HasSpan && parsed.LoadoutName == loadoutName) return null;

        var manager = SteamLaunchOptionLine.FindOtherManager(read.Line);
        return new Finding(manager is null ? null : manager.Name ?? manager.Command, read.Line);
    }

    /// <summary>
    /// The diagnostic for a game whose launch options aren't ours, or null when launching it any other way
    /// loses nothing: the files are real files in the game folder and the game needs no executable, arguments
    /// or launcher skip of its own.
    /// </summary>
    internal static Diagnostic? Evaluate(GameInstallation installation, string loadoutName, Finding finding, DeploymentPlan plan,
        IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, LaunchSpec? spec, AbsolutePath primaryExe, string lineToWrite)
    {
        var needsApp = LoadoutLaunchNeeds.NeedsApp(plan, spec);
        var gameNeedsLaunch = launcherSkipArguments.Count > 0 || spec is not null && (!spec.Exe.Equals(primaryExe) || spec.ExtraArgs.Length > 0);
        if (!needsApp && !gameNeedsLaunch && finding.OtherManager is null) return null;

        var skipped = DescribeSkipped(overrides, launcherSkipArguments, spec, primaryExe, needsApp);
        // With nothing else to list, the other manager is the whole reason for the warning.
        if (skipped.Length == 0 && finding.OtherManager is { } manager)
            skipped = $"* {manager} may change the game folder on its own.{Environment.NewLine}";
        return Create(installation, loadoutName, finding, skipped, lineToWrite);
    }

    /// <summary>
    /// The bullet list of what a launch that bypasses the app leaves out, built from what the game declares
    /// rather than from knowledge of any one game. The loadout bullet is only true when its files need the app.
    /// </summary>
    internal static string DescribeSkipped(IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, LaunchSpec? spec, AbsolutePath primaryExe, bool loadoutNeedsApp)
    {
        var sb = new StringBuilder();
        if (loadoutNeedsApp) sb.AppendLine("* The loadout is not applied before the game starts.");

        if (overrides.Count > 0)
            sb.AppendLine($"* The DLL overrides the game needs (`{WineParser.MergeDllOverrides("", overrides)}`) are not set.");

        if (spec is not null && !spec.Exe.Equals(primaryExe))
            sb.AppendLine($"* The game's own executable runs instead of `{spec.Exe.FileName}`.");

        if (spec is { ExtraArgs.Length: > 0 })
            sb.AppendLine("* The arguments the game needs are not passed.");

        if (launcherSkipArguments.Count > 0)
            sb.AppendLine($"* The game's launcher is not told to skip itself (`{string.Join(' ', launcherSkipArguments)}`).");

        return sb.ToString();
    }

    internal static Diagnostic Create(GameInstallation installation, string loadoutName, Finding finding, string skipped, string lineToWrite)
    {
        var gameName = installation.Game.DisplayName;
        var moreDetails = DescribeLaunchOptions(skipped, finding.CurrentLine, lineToWrite);
        Diagnostic diagnostic = finding.OtherManager is { } manager
            ? SteamLaunchDiagnostics.CreateOtherManagerLaunchesGame(gameName, loadoutName, manager)
            : SteamLaunchDiagnostics.CreateSteamSkipsLoadoutLaunch(gameName, loadoutName);
        return diagnostic with { Actions = [DiagnosticActions.UseLoadoutFromSteam()], MoreDetails = moreDetails };
    }

    /// <summary>
    /// The collapsed section: what the Steam start leaves out, then the two short lines the fix is about.
    /// The line to write is the short <c>run</c> line; the app builds the full command when the game starts,
    /// so that command never appears here.
    /// </summary>
    private static string DescribeLaunchOptions(string skipped, string? currentLine, string lineToWrite)
        => $"""
            What Steam's own start leaves out:

            {skipped}

            Steam's launch options now: {(string.IsNullOrWhiteSpace(currentLine) ? "empty" : $"`{currentLine}`")}

            "Use this loadout from Steam" writes: `{lineToWrite}`
            """;
}
