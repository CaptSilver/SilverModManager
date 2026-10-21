using NexusMods.Paths;
using NexusMods.Abstractions.Games;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Platform.Proton.Launch;

/// <inheritdoc />
public sealed class SteamLaunchOptionWriter(LocalConfigVdfWriter writer, IEnvironmentReader environment, IFileSystem fileSystem, IGameRegistry registry) : ISteamLaunchOptionWriter
{
    private AbsolutePath ShimPath => new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem)).ShimPath;

    /// <inheritdoc />
    public string ComposeLine(Loadout.ReadOnly loadout)
        => SteamLaunchOptionLine.Compose(ShimPath.ToString(), loadout.Name, WineDllOverrideRequirements.For(registry.ForceGetInstallation(loadout).Game, loadout));

    /// <inheritdoc />
    public string? MissingShimWarning => ShimPath.FileExists
        ? null
        : $"The launcher script {ShimPath} does not exist yet, so Steam could not start it. Start SilverModManager once to create it.";

    /// <inheritdoc />
    public LocalConfigVdfWriter.Result Write(GameInstallation installation, Loadout.ReadOnly loadout)
    {
        var located = installation.LocatorResult;
        if (located.Store != GameStore.Steam || located.LinuxCompatabilityDataProvider is not { } provider || !uint.TryParse(located.StoreIdentifier, out var appId))
        {
            return Refuse($"{installation.Game.DisplayName} is not a Steam game with a known Steam installation, so there are no launch options to write.");
        }

        if (MissingShimWarning is { } missing) return Refuse(missing);

        return writer.SetLauncherSpan(provider.SteamLocalConfigPath, appId, ShimPath.ToString(), loadout.Name, WineDllOverrideRequirements.For(installation.Game, loadout));
    }

    private static LocalConfigVdfWriter.Result Refuse(string refusal) => new(null, refusal, null);
}
