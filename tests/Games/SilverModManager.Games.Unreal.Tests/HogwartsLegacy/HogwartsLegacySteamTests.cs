using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unreal.Tests;

/// <summary>
/// Hogwarts Legacy found by the real Steam locator in a made-up library, laid out like the game: the launcher at the
/// game root and the real binary under Phoenix/Binaries/Win64.
/// </summary>
public class HogwartsLegacySteamTests(ITestOutputHelper helper) : ASteamInstalledGameTest<HogwartsLegacySteamTests, UnrealGame>(helper)
{
    private const uint AppId = 990080;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddUnrealGames();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var depot = new SteamDepot(990081, 5000000000000000001,
        [
            new SteamFile("HogwartsLegacy.exe", "launcher"),
            new SteamFile("Phoenix/Binaries/Win64/HogwartsLegacy.exe", "game binary"),
            new SteamFile("Phoenix/Content/Paks/Phoenix-WindowsNoEditor.pak", "base pak"),
        ]);
        return library.AddGame(AppId, "Hogwarts Legacy", "Hogwarts Legacy", 16000000, depot);
    }

    [Fact]
    public void TheSteamLocatorFindsTheGameByItsAppManifest()
    {
        GameInstallation.Game.GameId.Should().Be(GameId.From("Unreal.HogwartsLegacy"));
        GameInstallation.LocatorResult.StoreIdentifier.Should().Be(AppId.ToString());
        GameInstallation.Locations[LocationId.Game].Path.FileName.Should().Be("Hogwarts Legacy");
    }

    [Fact]
    public void TheSteamLocatorResolvesTheLauncherAtTheGameRootNotTheBinaryBelowIt()
    {
        var primary = Game.GetPrimaryFile(GameInstallation);

        primary.Should().Be(new GamePath(LocationId.Game, "HogwartsLegacy.exe"));
        GameInstallation.Locations[LocationId.Game].Path.Combine(primary.Path).FileExists.Should().BeTrue();
    }

    [Fact]
    public void ThePhoenixPaksAndBinariesFolderExistBelowTheGameFolder()
    {
        var game = GameInstallation.Locations[LocationId.Game].Path;

        (game / "Phoenix/Content/Paks").DirectoryExists().Should().BeTrue();
        (game / "Phoenix/Binaries/Win64/HogwartsLegacy.exe").FileExists.Should().BeTrue();
    }
}
