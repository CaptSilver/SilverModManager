using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Steam;

namespace NexusMods.Platform.Proton.Steam;

/// <summary>
/// Reports a Steam game whose launch options don't run the loadout, which is what makes Steam's own Play
/// button (and the app's Play, when it hands the game to Steam) skip everything the loadout sets up.
/// </summary>
/// <remarks>
/// Registered for every game; it only reports a Steam install whose <c>localconfig.vdf</c> can be located, and
/// only reads that file. A file it can't read says nothing about the game, so it is not a finding.
/// </remarks>
public sealed class SteamLaunchOptionEmitter(IToolManager tools, ILogger<SteamLaunchOptionEmitter> logger) : ILoadoutDiagnosticEmitter
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

        var skipped = DescribeSkipped(
            WineDllOverrideRequirements.For(installation.Game, loadout),
            GameLauncherSkip.For(installation.Game),
            await ResolveLaunchSpec(loadout, cancellationToken),
            installation.Locations.ToAbsolutePath(installation.Game.GetPrimaryFile(installation)));
        yield return Create(installation, loadout.Name, finding, skipped);
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
    /// The bullet list of what a launch that bypasses the app leaves out, built from what the game declares
    /// rather than from knowledge of any one game.
    /// </summary>
    internal static string DescribeSkipped(IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, LaunchSpec? spec, AbsolutePath primaryExe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("* The loadout is not applied before the game starts.");

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
