using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Steam;

namespace NexusMods.Platform.Proton.Steam;

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
public sealed class SteamLaunchOptionEmitter(IToolManager tools, IConnection connection, IDeploymentPlanStore plans, ILogger<SteamLaunchOptionEmitter> logger) : ILoadoutDiagnosticEmitter
{
    /// <param name="OtherManager">The launcher the line starts instead of the app, or null when the line is simply not ours.</param>
    internal sealed record Finding(string? OtherManager);

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
            await ResolveLaunchSpec(loadout, cancellationToken),
            installation.Locations.ToAbsolutePath(installation.Game.GetPrimaryFile(installation)));
        if (diagnostic is not null) yield return diagnostic;
    }

    private async ValueTask<LaunchSpec?> ResolveLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var runTool = tools.GetTools(loadout).OfType<IRunGameTool>().FirstOrDefault();
        if (runTool is null) return null;

        try
        {
            return await runTool.GetLaunchSpec(loadout, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The list still says what the game declares; only the run tool's exe and arguments are left out.
            logger.LogWarning(e, "Could not resolve the launch of {Loadout} for the Steam launch options diagnostic", loadout.Name);
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
        return new Finding(manager is null ? null : manager.Name ?? manager.Command);
    }

    /// <summary>
    /// The diagnostic for a game whose launch options aren't ours, or null when launching it any other way
    /// loses nothing: the files are real files in the game folder and the game needs no executable, arguments
    /// or launcher skip of its own.
    /// </summary>
    internal static Diagnostic? Evaluate(GameInstallation installation, string loadoutName, Finding finding, DeploymentPlan plan,
        IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, LaunchSpec? spec, AbsolutePath primaryExe)
    {
        var needsApp = LoadoutLaunchNeeds.NeedsApp(plan, spec);
        var gameNeedsLaunch = launcherSkipArguments.Count > 0 || spec is not null && (!spec.Exe.Equals(primaryExe) || spec.ExtraArgs.Length > 0);
        if (!needsApp && !gameNeedsLaunch && finding.OtherManager is null) return null;

        var skipped = DescribeSkipped(overrides, launcherSkipArguments, spec, primaryExe, needsApp);
        // With nothing else to list, the other manager is the whole reason for the warning.
        if (skipped.Length == 0 && finding.OtherManager is { } manager)
            skipped = $"* {manager} may change the game folder on its own.{Environment.NewLine}";
        return Create(installation, loadoutName, finding, skipped);
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
            sb.AppendLine($"* The arguments the game needs (`{string.Join(' ', spec.ExtraArgs)}`) are not passed.");

        if (launcherSkipArguments.Count > 0)
            sb.AppendLine($"* The game's launcher is not told to skip itself (`{string.Join(' ', launcherSkipArguments)}`).");

        return sb.ToString();
    }

    internal static Diagnostic Create(GameInstallation installation, string loadoutName, Finding finding, string skipped)
    {
        var gameName = installation.Game.DisplayName;
        System.Collections.Immutable.ImmutableArray<DiagnosticAction> actions = [DiagnosticActions.UseLoadoutFromSteam()];
        return finding.OtherManager is { } manager
            ? SteamLaunchDiagnostics.CreateOtherManagerLaunchesGame(gameName, loadoutName, manager, skipped) with { Actions = actions }
            : SteamLaunchDiagnostics.CreateSteamSkipsLoadoutLaunch(gameName, loadoutName, skipped) with { Actions = actions };
    }
}
