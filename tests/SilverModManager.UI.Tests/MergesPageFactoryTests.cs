using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.App.UI.Pages.Merges;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests;

public class MergesPageFactoryTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task ALoadoutOfAGameWithoutAMergeEngineGetsNoMergesPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "no-engine");
        var factory = new MergesPageFactory(Provider);

        factory.GetDiscoveryDetails(new LoadoutContext { LoadoutId = loadout.LoadoutId }).Should().BeEmpty();
    }

    [Fact]
    public async Task ALoadoutOfAGameWithAMergeEngineGetsTheMergesPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "merging");
        var engine = Substitute.For<IMergeEngine>();
        engine.Serves(Arg.Any<IGameData>()).Returns(true);

        // GetDiscoveryDetails reads only the connection and the engine registry; the page factory constructor
        // also resolves a window manager.
        var services = new ServiceCollection()
            .AddSingleton(Provider.GetRequiredService<IConnection>())
            .AddSingleton(Substitute.For<IWindowManager>())
            .AddSingleton(engine)
            .AddSingleton<MergeEngines>()
            .BuildServiceProvider();

        var details = new MergesPageFactory(services).GetDiscoveryDetails(new LoadoutContext { LoadoutId = loadout.LoadoutId }).ToArray();

        details.Should().ContainSingle().Which!.ItemName.Should().Be("Merges");
    }

    [Fact]
    public void ThePageIsRegisteredWithTheWorkspace()
        => Provider.GetServices<IPageFactory>().Should().ContainSingle(f => f is MergesPageFactory);
}
