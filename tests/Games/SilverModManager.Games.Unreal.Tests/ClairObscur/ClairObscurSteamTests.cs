using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unreal.Tests;

/// <summary>
/// Clair Obscur: Expedition 33 found by the real Steam locator in a made-up library, laid out like the game: the launcher at the
/// game root and the real binary under Sandfall/Binaries/Win64.
/// </summary>
public class ClairObscurSteamTests(ITestOutputHelper helper) : ASteamInstalledGameTest<ClairObscurSteamTests, UnrealGame>(helper)
{
    private const uint AppId = 1903340;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddUnrealGames();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var depot = new SteamDepot(1903341, 5000000000000000002,
        [
            new SteamFile("Expedition33_Steam.exe", "launcher"),
            new SteamFile("Sandfall/Binaries/Win64/SandFall-Win64-Shipping.exe", "game binary"),
            new SteamFile("Sandfall/Content/Paks/pakchunk0-Windows.pak", "base pak"),
        ]);
        return library.AddGame(AppId, "Expedition 33", "Clair Obscur: Expedition 33", 16000000, depot);
    }

    [Fact]
    public void TheSteamLocatorFindsTheGameByItsAppManifest()
    {
        GameInstallation.Game.GameId.Should().Be(GameId.From("Unreal.ClairObscurExpedition33"));
        GameInstallation.LocatorResult.StoreIdentifier.Should().Be(AppId.ToString());
        GameInstallation.Locations[LocationId.Game].Path.FileName.Should().Be("Expedition 33");
    }

    [Fact]
    public void TheSteamLocatorResolvesTheLauncherAtTheGameRootNotTheBinaryBelowIt()
    {
        var primary = Game.GetPrimaryFile(GameInstallation);

        primary.Should().Be(new GamePath(LocationId.Game, "Expedition33_Steam.exe"));
        GameInstallation.Locations[LocationId.Game].Path.Combine(primary.Path).FileExists.Should().BeTrue();
    }

    [Fact]
    public void TheSandfallPaksAndBinariesFolderExistBelowTheGameFolder()
    {
        var game = GameInstallation.Locations[LocationId.Game].Path;

        (game / "Sandfall/Content/Paks").DirectoryExists().Should().BeTrue();
        (game / "Sandfall/Binaries/Win64/SandFall-Win64-Shipping.exe").FileExists.Should().BeTrue();
    }
}
