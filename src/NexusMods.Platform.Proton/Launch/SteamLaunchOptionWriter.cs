using NexusMods.Paths;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

/// <inheritdoc />
public sealed class SteamLaunchOptionWriter(LocalConfigVdfWriter writer, IEnvironmentReader environment, IFileSystem fileSystem) : ISteamLaunchOptionWriter
{
    private AbsolutePath ShimPath => new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem)).ShimPath;

    /// <inheritdoc />
    public string ComposeLine(string loadoutName) => SteamLaunchOptionLine.Compose(ShimPath.ToString(), loadoutName);

    /// <inheritdoc />
    public string? MissingShimWarning => ShimPath.FileExists
        ? null
        : $"The launcher script {ShimPath} does not exist yet, so Steam could not start it. Start SilverModManager once to create it.";

    /// <inheritdoc />
    public LocalConfigVdfWriter.Result Write(GameInstallation installation, string loadoutName)
    {
        var located = installation.LocatorResult;
        if (located.Store != GameStore.Steam || located.LinuxCompatabilityDataProvider is not { } provider || !uint.TryParse(located.StoreIdentifier, out var appId))
        {
            return Refuse($"{installation.Game.DisplayName} is not a Steam game with a known Steam installation, so there are no launch options to write.");
        }

        if (MissingShimWarning is { } missing) return Refuse(missing);

        return writer.SetLauncherSpan(provider.SteamLocalConfigPath, appId, ShimPath.ToString(), loadoutName);
    }

    private static LocalConfigVdfWriter.Result Refuse(string refusal) => new(null, refusal, null);
}
