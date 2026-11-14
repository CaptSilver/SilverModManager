using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Framework;
using SilverModManager.UI.Tests.Input;

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

    [Fact]
    public async Task TheArrowButtonsAreFocusableSoAActivatesThem()
    {
        var variety = new LoadOrderViewModelTests.ListedVariety(
            new LoadOrderViewModelTests.FakeItem(true, false, "a.mod") { SortIndex = 0 },
            new LoadOrderViewModelTests.FakeItem(true, false, "b.mod") { SortIndex = 1 }) { RecordsMoves = true };
        var vm = new LoadOrderViewModel(Provider, variety, LoadoutId.From(1));
        var view = await OnUi(() => Task.FromResult(new LoadOrderView { ViewModel = vm }));
        PadRig? rig = null;
        try
        {
            rig = await OnUi(() =>
            {
                var created = new PadRig(view, () => [new ActivateBinding()]);
                created.Show();
                return Task.FromResult(created);
            });

            StandardButton? down = null;
            await EventuallyOnUi(() =>
            {
                rig.Window.UpdateLayout();
                down = view.GetVisualDescendants().OfType<StandardButton>().FirstOrDefault(b => b.Name == "DownButton");
                down.Should().NotBeNull("the first row is realized");
            });

            await OnUi(() =>
            {
                down!.Focusable.Should().BeTrue();
                down.Focus(NavigationMethod.Tab).Should().BeTrue();
                rig.Send(new SilverModManager.Platform.Gamepad.ButtonDown(TimeSpan.Zero, SilverModManager.Platform.Gamepad.GamepadButton.A));
                return Task.CompletedTask;
            });

            await EventuallyOnUi(() => variety.Deltas.Should().Contain(+1, "A pressed the down arrow of the focused row"));
        }
        finally
        {
            if (rig is not null) await OnUi(() => { rig.Dispose(); return Task.CompletedTask; });
        }
    }
}
