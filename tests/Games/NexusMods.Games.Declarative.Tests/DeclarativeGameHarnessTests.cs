using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>A definition is located through the stubbed locator and gets a loadout, found by its game id.</summary>
public class DeclarativeGameHarnessTests(IServiceProvider serviceProvider) : ADeclarativeGameTest(serviceProvider, Samples.HarnessId)
{
    [Fact]
    public void TheStubbedLocatorFindsTheDefinition()
    {
        Game.Should().BeOfType<DeclarativeGame>();
        GameInstallation.Game.GameId.Should().Be(GameId.From(Samples.HarnessId));
        GameInstallation.LocatorResult.LocatorIds.Should().ContainSingle().Which.Value.Should().Be("StubbedGameState.zip");
    }

    [Fact]
    public void WithoutACapabilitySupplyingOne_TheSynchronizerIsTheDefault()
    {
        Game.Synchronizer.Should().BeOfType<DefaultSynchronizer>();
    }

    [Fact]
    public async Task ALoadoutIsCreatedForIt()
    {
        var loadout = await CreateLoadout();

        loadout.InstallationInstance.Game.GameId.Should().Be(GameId.From(Samples.HarnessId));
        loadout.InstallationInstance.GetGame().Should().BeSameAs(Game);
    }
}
