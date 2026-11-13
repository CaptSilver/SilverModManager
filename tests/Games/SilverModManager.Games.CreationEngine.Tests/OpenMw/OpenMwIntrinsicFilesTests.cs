using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Sdk.Games;
using Xunit;
using static SilverModManager.Games.CreationEngine.OpenMw.OpenMwConstants;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwIntrinsicFilesTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwIntrinsicFilesTests>(helper)
{
    [Fact]
    public async Task OffersTheConfigFileOnly()
    {
        var loadout = await CreateLoadout();

        ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(loadout).Keys.Should().BeEquivalentTo([OpenMwConfigFile]);
    }

    [Fact]
    public void UsesTheOpenMwMechanismAndIgnoresTheMasterFlag()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<OpenMwMechanism>().Which.OrdersByTimestamp.Should().BeFalse();
        Game.MasterFlagIgnored(GameInstallation).Should().BeTrue();
    }

    [Fact]
    public void SortsAsOpenMw() => Game.LootGame(GameInstallation).Should().Be(LootGame.OpenMW);

    [Theory]
    [InlineData("settings.cfg", true)]
    [InlineData("launcher.cfg", true)]
    [InlineData("sub/other.txt", true)]
    [InlineData("openmw.cfg", false)]
    public void OnlyTheConfigFileIsIndexed(string path, bool filtered) =>
        Game.Synchronizer.GamePathFilter.ShouldFilter(new GamePath(OpenMwConfig, path)).Should().Be(filtered);

    [Fact]
    public void TheMorrowindFolderIsAddedAsAFilteredPath() =>
        Game.Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, "Data Files/Morrowind.esm")).Should().BeTrue();
}
