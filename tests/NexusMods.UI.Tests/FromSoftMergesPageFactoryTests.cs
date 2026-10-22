using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Pages.FromSoftMerges;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Games;

namespace NexusMods.UI.Tests;

public class FromSoftMergesPageFactoryTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task ALoadoutOfAnotherGetsNoMergesPage()
    {
        // The UI host's only game is not a FromSoft game.
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "not-fromsoft");
        var factory = new FromSoftMergesPageFactory(Provider);

        factory.GetDiscoveryDetails(new LoadoutContext { LoadoutId = loadout.LoadoutId }).Should().BeEmpty();
    }

    [Fact]
    public void ThePageIsRegisteredWithTheWorkspace()
        => Provider.GetServices<IPageFactory>().Should().ContainSingle(f => f is FromSoftMergesPageFactory);
}
