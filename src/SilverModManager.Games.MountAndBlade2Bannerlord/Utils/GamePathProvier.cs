using Bannerlord.LauncherManager;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Utils;

public static class GamePathProvier
{
    private static RelativePath GetConfiguration(GameStore store) => LauncherManagerHandler.GetConfigurationByPlatform(LauncherManagerHandler.FromStore(Converter.ToGameStoreTW(store)));

    private static RelativePath GetBinPath(GameStore store) => RelativePath.FromUnsanitizedInput("bin").Join(GetConfiguration(store));

    public static GamePath PrimaryLauncherFile(GameStore store) => new(LocationId.Game, GetBinPath(store).Join("TaleWorlds.MountAndBlade.Launcher.exe"));

    public static GamePath PrimaryStandaloneFile(GameStore store) => new(LocationId.Game, GetBinPath(store).Join(Constants.BannerlordExecutable));

    public static GamePath BLSEStandaloneFile(GameStore store) => new(LocationId.Game, GetBinPath(store).Join("Bannerlord.BLSE.Standalone.exe"));

    public static GamePath BLSELauncherFile(GameStore store) => new(LocationId.Game, GetBinPath(store).Join("Bannerlord.BLSE.Launcher.exe"));

    public static GamePath BLSELauncherExFile(GameStore store) => new(LocationId.Game, GetBinPath(store).Join("Bannerlord.BLSE.LauncherEx.exe"));
}
