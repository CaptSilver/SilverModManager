using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The sorting tabs and the "fix it on this page" links both ask <see cref="LoadOrderPage"/>, so these run it against
/// the games as the app registers them.
/// </summary>
public class LoadOrderPageVisibilityTests(IServiceProvider provider) : AUiTest(provider)
{
    private IGame GameOf(string id)
        => Provider.GetServices<IGame>().Single(game => game.GameId == GameId.From(id));

    [Fact]
    public void StarsectorHasNoLoadOrderPageAndSendsPriorityEditsToFileConflicts()
    {
        var starsector = GameOf("LoadOrderFile.Starsector");

        LoadOrderPage.IsShown(starsector).Should().BeFalse();
        DefaultPriorityOrderSink.PriorityPage(starsector).Should().Be("File Conflicts");
    }

    [Fact]
    public void RimWorldKeepsItsLoadOrderPage()
    {
        var rimWorld = GameOf("LoadOrderFile.RimWorld");

        LoadOrderPage.IsShown(rimWorld).Should().BeTrue();
        DefaultPriorityOrderSink.PriorityPage(rimWorld).Should().Be("Load Order");
    }
}
