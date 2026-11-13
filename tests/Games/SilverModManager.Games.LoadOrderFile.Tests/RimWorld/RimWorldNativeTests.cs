using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.LoadOrderFile.Tests;

/// <summary>The Linux build of RimWorld launches from a script and keeps its config below the XDG config folder, not in a prefix.</summary>
public class RimWorldNativeTests(ITestOutputHelper outputHelper)
    : ALoadOrderFileGameTests<RimWorldNativeTests>(outputHelper, LoadOrderFileGames.RimWorld)
{
    protected override IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { ["start_RimWorld.sh"] = [] },
            locationsFileSystem: IsolatedXdgFolders.Create,
            platform: OSPlatform.Linux);

    [Fact]
    public void ThePrimaryFileIsTheLaunchScript()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "start_RimWorld.sh"));
    }

    [Fact]
    public void TheConfigLocationIsBelowXdgConfig()
    {
        GameInstallation.Locations[SimpleLocations.LocalAppDataLow].Path.ToString()
            .Should().EndWith("/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config");
    }

    [Fact]
    public async Task ApplyWritesTheOrderFileThere()
    {
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        GameInstallation.Locations.ToAbsolutePath(Definition.OrderFile.ToGamePath()).FileExists.Should().BeTrue();
        GameInstallation.Locations[SimpleLocations.LocalAppDataLow].Path.Combine("ModsConfig.xml").FileExists.Should().BeTrue();
    }
}
