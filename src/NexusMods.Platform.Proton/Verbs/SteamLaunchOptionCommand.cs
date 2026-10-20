using NexusMods.Abstractions.Cli;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Platform.Proton.Verbs;

/// <summary>
/// Prints the Steam launch options line that starts a loadout, and with <c>--write</c> stores it in Steam's
/// <c>localconfig.vdf</c>. Every failure exits with <see cref="LaunchSession.FailureExitCode"/>, like <c>run</c>.
/// </summary>
public sealed class SteamLaunchOptionCommand(LoadoutResolver resolver, ISteamLaunchOptionWriter launchOptions)
{
    // The resolver only reads the command to look for a SteamLaunch app id; there is none to find here.
    private static readonly string[] NoCommand = ["%command%"];

    public async Task<int> RunAsync(IRenderer renderer, string loadout, string? game, bool write)
    {
        var resolution = resolver.Resolve(new LaunchArgs(loadout, game, NoCommand));
        if (!resolution.IsResolved)
        {
            await renderer.TextLine(resolution.Error!);
            return resolution.ExitCode;
        }

        var name = resolution.Loadout!.Value.Name;
        if (write)
        {
            return await Write(renderer, resolution.Installation!, name);
        }

        await renderer.TextLine(launchOptions.ComposeLine(name));
        await renderer.TextLine("Paste this into the game's Properties > Launch Options in Steam, or run again with --write while Steam is closed.");
        await renderer.TextLine("The line runs the current deploy of the loadout.");
        if (launchOptions.MissingShimWarning is { } missing) await renderer.TextLine(missing);
        return 0;
    }

    private async Task<int> Write(IRenderer renderer, GameInstallation installation, string loadoutName)
    {
        var result = launchOptions.Write(installation, loadoutName);
        if (result.IsRefused)
        {
            await renderer.TextLine(result.Refusal!);
            return LaunchSession.FailureExitCode;
        }

        // A write only succeeds for a Steam game, which always carries its config path.
        var localConfig = installation.LocatorResult.LinuxCompatabilityDataProvider!.SteamLocalConfigPath;
        await renderer.TextLine($"Set the launch options of {installation.Game.DisplayName} in {localConfig}:");
        await renderer.TextLine(result.Line!);
        await renderer.TextLine($"The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.");
        return 0;
    }
}
