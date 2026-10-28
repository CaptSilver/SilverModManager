using System.ComponentModel;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The trophy bar beside the Load Order grid: its arrow points at the end of the list where the winner is.</summary>
[Collection(HeadlessUi.Collection)]
public class LoadOrderViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(bool Up, bool Down)> ArrowsFor(ListSortDirection direction, IndexOverrideBehavior behavior)
    {
        var variety = new LoadOrderViewModelTests.FakeVariety { SortDirectionDefault = direction, IndexOverrideBehavior = behavior };
        var vm = new LoadOrderViewModel(Provider, variety, LoadoutId.From(1));

        return await OnUi(() =>
        {
            var view = new LoadOrderView { ViewModel = vm };
            ShowRendered(Track(new Window { Width = 800, Height = 600, Content = view }));
            return Task.FromResult((view.FindControl<Control>("ArrowUpIcon")!.IsVisible, view.FindControl<Control>("ArrowDownIcon")!.IsVisible));
        });
    }

    [Fact]
    public async Task TheRedModPageInItsDefaultDirectionPointsDownAtTheWinner()
    {
        var redMod = Provider.GetServices<IGame>().OfType<Cyberpunk2077Game>().Single().SortOrderManager.GetSortOrderVarieties().OfType<RedModSortOrderVariety>().Single();

        (await ArrowsFor(redMod.SortDirectionDefault, redMod.IndexOverrideBehavior)).Should().Be((false, true));
    }

    [Fact]
    public async Task APluginPageInItsDefaultDirectionPointsDownAtTheWinner()
        => (await ArrowsFor(ListSortDirection.Ascending, IndexOverrideBehavior.GreaterIndexWins)).Should().Be((false, true));

    [Fact]
    public async Task APageThatPutsTheWinnerOnTopPointsUp()
        => (await ArrowsFor(ListSortDirection.Descending, IndexOverrideBehavior.GreaterIndexWins)).Should().Be((true, false));
}
