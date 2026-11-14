using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

public class BaldursGate3ModIoTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void TheGameIsOnModIoAndItsOwnModManagerSyncsSubscriptions()
    {
        var game = serviceProvider.GetServices<IGame>().OfType<global::SilverModManager.Games.Larian.BaldursGate3.BaldursGate3>().Single();

        var support = ModIoGame.For(game);

        support.Should().NotBeNull();
        support!.GameSyncsSubscriptions.Should().BeTrue("the in-game Mod Manager downloads the account's mod.io subscriptions");
        support.ModIoNameId.Should().Be("baldursgate3");
        support.ModIoGameId.Should().NotBe(0);
    }
}
