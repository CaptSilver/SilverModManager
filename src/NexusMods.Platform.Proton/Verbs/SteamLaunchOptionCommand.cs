using NexusMods.Abstractions.Cli;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Platform.Proton.Verbs;

/// <summary>
/// Prints the Steam launch options line that starts a loadout, and with <c>--write</c> stores it in Steam's
/// <c>localconfig.vdf</c>. Every failure exits with <see cref="LaunchSession.FailureExitCode"/>, like <c>run</c>.
/// </summary>
public sealed class SteamLaunchOptionCommand(LoadoutResolver resolver, LocalConfigVdfWriter writer, IEnvironmentReader environment, IFileSystem fileSystem)
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
        var shim = new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem)).ShimPath;
        var shimExists = shim.FileExists;

        if (write)
        {
            return await Write(renderer, resolution.Installation!, shim, shimExists, name);
        }

        await renderer.TextLine(SteamLaunchOptionLine.Compose(shim.ToString(), name));
        await renderer.TextLine("Paste this into the game's Properties > Launch Options in Steam, or run again with --write while Steam is closed.");
        await renderer.TextLine("The line runs the current deploy of the loadout.");
        if (!shimExists) await renderer.TextLine(MissingShim(shim));
        return 0;
    }

    private async Task<int> Write(IRenderer renderer, GameInstallation installation, AbsolutePath shim, bool shimExists, string loadoutName)
    {
        var located = installation.LocatorResult;
        if (located.Store != GameStore.Steam || located.LinuxCompatabilityDataProvider is not { } provider || !uint.TryParse(located.StoreIdentifier, out var appId))
        {
            await renderer.TextLine($"{installation.Game.DisplayName} is not a Steam game with a known Steam installation, so there are no launch options to write.");
            return LaunchSession.FailureExitCode;
        }

        if (!shimExists)
        {
            await renderer.TextLine(MissingShim(shim));
            return LaunchSession.FailureExitCode;
        }

        var result = writer.SetLauncherSpan(provider.SteamLocalConfigPath, appId, shim.ToString(), loadoutName);
        if (result.IsRefused)
        {
            await renderer.TextLine(result.Refusal!);
            return LaunchSession.FailureExitCode;
        }

        await renderer.TextLine($"Set the launch options of {installation.Game.DisplayName} in {provider.SteamLocalConfigPath}:");
        await renderer.TextLine(result.Line!);
        await renderer.TextLine($"The previous file was kept as {result.Backup}. The line runs the current deploy of the loadout.");
        return 0;
    }

    private static string MissingShim(AbsolutePath shim)
        => $"The launcher script {shim} does not exist yet, so Steam could not start it. Start SilverModManager once to create it.";
}
