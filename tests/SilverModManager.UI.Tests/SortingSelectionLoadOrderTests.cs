using DynamicData.Kernel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Sdk.Games;
using R3;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The Rules tab of a game with no sort order variety of its own still offers a Load Order page, over the mod
/// priority rows; a game with a variety keeps its own pages.
/// </summary>
public class SortingSelectionLoadOrderTests(IServiceProvider provider) : AUiTest(provider)
{
    private SortingSelectionViewModel Page(SilverModManager.Sdk.Loadouts.Loadout.ReadOnly loadout)
        => new(Provider, Substitute.For<IWindowManager>(), loadout.LoadoutId, Optional<Observable<bool>>.None);

    [Fact]
    public async Task AGameWithoutVarietiesGetsTheLoadOrderPageBeforeFileConflicts()
    {
        var loadout = await SeedLoadoutRow<EldenRingGame>(GameStore.Steam, "No varieties");

        var page = Page(loadout);

        page.RulesViewModels.Should().HaveCount(2);
        page.RulesViewModels[0].Should().BeOfType<LoadOrderViewModel>()
            .Which.SortOrderVariety.Should().BeOfType<ModPriorityOrderVariety>();
        page.RulesViewModels[1].Should().BeOfType<FileConflictsViewModel>();
    }

    [Fact]
    public async Task TheLoadOrderPageSurvivesTheActivationRefresh()
    {
        var loadout = await SeedLoadoutRow<EldenRingGame>(GameStore.Steam, "Activated");
        var page = Page(loadout);

        using var _ = page.Activator.Activate();

        page.RulesViewModels.Select(vm => vm.GetType()).Should().Equal(typeof(LoadOrderViewModel), typeof(FileConflictsViewModel));
    }

    [Fact]
    public async Task AGameWithAVarietyKeepsItsOwnPagesOnly()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Cyberpunk");

        var page = Page(loadout);

        page.RulesViewModels.OfType<LoadOrderViewModel>().Should().NotContain(vm => vm.SortOrderVariety is ModPriorityOrderVariety);
        page.RulesViewModels.Should().EndWith(page.RulesViewModels.OfType<FileConflictsViewModel>().Single());
    }
}
