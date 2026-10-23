using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>
/// Prints the Steam launch options line that starts a loadout, and with <c>--write</c> stores it in Steam's
/// <c>localconfig.vdf</c>. Every failure exits with <see cref="LaunchSession.FailureExitCode"/>, like <c>run</c>.
/// </summary>
public sealed class SteamLaunchOptionCommand(LoadoutResolver resolver, ISteamLaunchOptionWriter launchOptions)
{
    // The resolver only reads the command to look for a SteamLaunch app id; there is none to find here.
    private static readonly string[] NoCommand = ["%command%"];

    public async Task<int> RunAsync(IRenderer renderer, string loadout, string? game, bool write, string? replaceOtherManager = null)
    {
        if (replaceOtherManager is not null && !write)
        {
            await renderer.TextLine("--replace-other-manager needs --write.");
            return LaunchSession.FailureExitCode;
        }

        var resolution = resolver.Resolve(new LaunchArgs(loadout, game, NoCommand));
        if (!resolution.IsResolved)
        {
            await renderer.TextLine(resolution.Error!);
            return resolution.ExitCode;
        }

        var loadoutValue = resolution.Loadout!.Value;
        if (write)
        {
            return replaceOtherManager is null
                ? await Write(renderer, resolution.Installation!, loadoutValue)
                : await Replace(renderer, resolution.Installation!, loadoutValue, replaceOtherManager);
        }

        await renderer.TextLine(launchOptions.ComposeLine(loadoutValue));
        await renderer.TextLine("Paste this into the game's Properties > Launch Options in Steam, or run again with --write while Steam is closed.");
        await renderer.TextLine("The line runs the current deploy of the loadout.");
        var overrides = WineDllOverrideRequirements.For(resolution.Installation!.Game, loadoutValue);
        if (overrides.Count > 0)
        {
            await renderer.TextLine($"It also sets {WineParser.WineDllOverridesEnvironmentVariableName} to {WineParser.MergeDllOverrides("", overrides)}, which {resolution.Installation.Game.DisplayName} needs for its mods.");
        }

        if (GameLauncherSkip.For(resolution.Installation.Game) is { Count: > 0 } skip)
        {
            await renderer.TextLine($"It also passes {string.Join(' ', skip)} so {resolution.Installation.Game.DisplayName} starts without its launcher window.");
        }

        if (launchOptions.MissingShimWarning is { } missing) await renderer.TextLine(missing);
        return 0;
    }

    private async Task<int> Write(IRenderer renderer, GameInstallation installation, Loadout.ReadOnly loadout)
    {
        var result = launchOptions.Write(installation, loadout);
        if (result.IsRefused)
        {
            await renderer.TextLine(result.Refusal!);
            if (result.Replacement is { } replacement) await PrintReplacementOffer(renderer, installation, loadout, replacement);
            return LaunchSession.FailureExitCode;
        }

        // A write only succeeds for a Steam game, which always carries its config path.
        var localConfig = installation.LocatorResult.LinuxCompatabilityDataProvider!.SteamLocalConfigPath;
        await renderer.TextLine($"Set the launch options of {installation.Game.DisplayName} in {localConfig}:");
        await renderer.TextLine(result.Line!);
        await renderer.TextLine($"The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.");
        return 0;
    }

    private async Task PrintReplacementOffer(IRenderer renderer, GameInstallation installation, Loadout.ReadOnly loadout, LocalConfigVdfWriter.Replacement replacement)
    {
        await renderer.TextLine($"These launch options start {replacement.ManagerName ?? "another launcher"}:");
        await renderer.TextLine(replacement.OldLine);
        await renderer.TextLine("Replacing them would set:");
        await renderer.TextLine(replacement.NewLine);

        var command = $"silvermod steam-launch-option --loadout {SteamLaunchOptionLine.ShellQuote(loadout.Name)}";
        // The game is named by app id: a display name could match more than one installation.
        if (installation.LocatorResult.StoreIdentifier is { Length: > 0 } appId) command += $" --game {SteamLaunchOptionLine.ShellQuote(appId)}";
        command += $" --write --replace-other-manager {SteamLaunchOptionLine.ShellQuote(replacement.OldLine)}";
        await renderer.TextLine("To replace them, run:");
        await renderer.TextLine(command);

        if (launchOptions.ReplacedLineRecord(installation) is { } record)
        {
            await renderer.TextLine($"The old line is written to {record} before anything changes.");
        }
    }

    private async Task<int> Replace(IRenderer renderer, GameInstallation installation, Loadout.ReadOnly loadout, string confirmedOldLine)
    {
        var result = launchOptions.Replace(installation, loadout, confirmedOldLine);
        if (result.IsRefused)
        {
            await renderer.TextLine(result.Refusal!);
            return LaunchSession.FailureExitCode;
        }

        var localConfig = installation.LocatorResult.LinuxCompatabilityDataProvider!.SteamLocalConfigPath;
        await renderer.TextLine($"Replaced the launch options of {installation.Game.DisplayName} in {localConfig}:");
        await renderer.TextLine(result.Replacement!.OldLine);
        await renderer.TextLine("with:");
        await renderer.TextLine(result.Line!);
        await renderer.TextLine($"The old line is recorded in {result.ReplacedLineRecord}. The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.");
        return 0;
    }
}
