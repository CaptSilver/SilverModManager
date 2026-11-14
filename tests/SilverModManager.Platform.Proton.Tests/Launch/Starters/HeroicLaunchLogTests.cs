using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch.Starters;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch.Starters;

public class HeroicLaunchLogTests
{
    private static string Home => FileSystem.Shared.GetKnownPath(KnownPath.HomeDirectory).ToString();

    [Fact]
    public void PathFor_ANativeHeroicUsesXdgStateHome()
    {
        var client = new StoreClient(StoreClientKind.Heroic, "Sugar", false);

        HeroicLaunchLog.PathFor(FileSystem.Shared, client, "legendary", "/state").ToString()
            .Should().Be("/state/Heroic/logs/games/Sugar_legendary/launch.log");
    }

    [Fact]
    public void PathFor_ANativeHeroicWithoutXdgStateHomeUsesLocalState()
    {
        var client = new StoreClient(StoreClientKind.Heroic, "Sugar", false);

        HeroicLaunchLog.PathFor(FileSystem.Shared, client, "gog", xdgStateHome: null).ToString()
            .Should().Be($"{Home}/.local/state/Heroic/logs/games/Sugar_gog/launch.log");
    }

    [Fact]
    public void PathFor_TheFlatpakIgnoresTheHostsXdgStateHome()
    {
        var client = new StoreClient(StoreClientKind.Heroic, "B0ABC", true);

        HeroicLaunchLog.PathFor(FileSystem.Shared, client, "nile", "/state").ToString()
            .Should().Be($"{Home}/.var/app/com.heroicgameslauncher.hgl/.local/state/Heroic/logs/games/B0ABC_nile/launch.log");
    }

    [Theory]
    [InlineData("Epic Games Store", "legendary")]
    [InlineData("GOG", "gog")]
    [InlineData("Amazon", "nile")]
    public void RunnerOf_NamesHeroicsRunnerForTheStore(string store, string runner)
        => HeroicLaunchLog.RunnerOf(GameStore.From(store)).Should().Be(runner);

    [Fact]
    public void RunnerOf_RefusesAStoreHeroicDoesNotServe()
    {
        var act = () => HeroicLaunchLog.RunnerOf(GameStore.Steam);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
