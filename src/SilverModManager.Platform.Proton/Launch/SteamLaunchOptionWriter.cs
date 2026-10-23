using NexusMods.Paths;
using NexusMods.Abstractions.Games;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Platform.Proton.Launch;

/// <inheritdoc />
public sealed class SteamLaunchOptionWriter(LocalConfigVdfWriter writer, IEnvironmentReader environment, IFileSystem fileSystem, IGameRegistry registry) : ISteamLaunchOptionWriter
{
    private AbsolutePath ShimPath => new LauncherShim(LauncherShim.ResolveDataHome(environment, fileSystem)).ShimPath;

    /// <inheritdoc />
    public string ComposeLine(Loadout.ReadOnly loadout)
        => SteamLaunchOptionLine.Compose(ShimPath.ToString(), loadout.Name, WineDllOverrideRequirements.For(registry.ForceGetInstallation(loadout).Game, loadout), GameLauncherSkip.For(registry.ForceGetInstallation(loadout).Game));

    /// <inheritdoc />
    public string? MissingShimWarning => ShimPath.FileExists
        ? null
        : $"The launcher script {ShimPath} does not exist yet, so Steam could not start it. Start SilverModManager once to create it.";

    /// <inheritdoc />
    public LocalConfigVdfWriter.Result Write(GameInstallation installation, Loadout.ReadOnly loadout)
    {
        if (Locate(installation) is not { } game) return Refuse(NotSteamGame(installation));
        if (MissingShimWarning is { } missing) return Refuse(missing);

        return writer.SetLauncherSpan(game.Config, game.AppId, ShimPath.ToString(), loadout.Name, WineDllOverrideRequirements.For(installation.Game, loadout), GameLauncherSkip.For(installation.Game));
    }

    /// <inheritdoc />
    public LocalConfigVdfWriter.Result Replace(GameInstallation installation, Loadout.ReadOnly loadout, string confirmedOldLine)
    {
        if (Locate(installation) is not { } game) return Refuse(NotSteamGame(installation));
        if (MissingShimWarning is { } missing) return Refuse(missing);

        return writer.ReplaceLaunchOptions(game.Config, game.AppId, ShimPath.ToString(), loadout.Name, WineDllOverrideRequirements.For(installation.Game, loadout), GameLauncherSkip.For(installation.Game), confirmedOldLine, RecordFor(game.AppId));
    }

    /// <inheritdoc />
    public AbsolutePath? ReplacedLineRecord(GameInstallation installation)
        => Locate(installation) is { } game ? RecordFor(game.AppId) : null;

    // Beside the shim's own silvermod/bin: our data, not Steam's folder and not the database.
    private AbsolutePath RecordFor(uint appId)
        => LauncherShim.ResolveDataHome(environment, fileSystem).Combine("silvermod/steam-launch-options").Combine($"{appId}.replaced");

    internal static (AbsolutePath Config, uint AppId)? Locate(GameInstallation installation)
    {
        var located = installation.LocatorResult;
        if (located.Store != GameStore.Steam || located.LinuxCompatabilityDataProvider is not { } provider || !uint.TryParse(located.StoreIdentifier, out var appId)) return null;
        return (provider.SteamLocalConfigPath, appId);
    }

    private static string NotSteamGame(GameInstallation installation)
        => $"{installation.Game.DisplayName} is not a Steam game with a known Steam installation, so there are no launch options to write.";

    private static LocalConfigVdfWriter.Result Refuse(string refusal) => new(null, refusal, null);
}
