using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.LoadOrderFile.Tests;

/// <summary>The Linux build of Starsector launches from a script and keeps its mods in the game folder like the Windows build.</summary>
public class StarsectorNativeTests(ITestOutputHelper outputHelper)
    : ALoadOrderFileGameTests<StarsectorNativeTests>(outputHelper, LoadOrderFileGames.Starsector)
{
    protected override IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { ["starsector.sh"] = [] },
            locationsFileSystem: IsolatedXdgFolders.Create,
            platform: OSPlatform.Linux);

    [Fact]
    public void ThePrimaryFileIsTheLaunchScript()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "starsector.sh"));
    }

    [Fact]
    public async Task ApplyWritesTheOrderFileInTheGameFolder()
    {
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        GameInstallation.Locations[LocationId.Game].Path.Combine("mods/enabled_mods.json").FileExists.Should().BeTrue();
    }
}
