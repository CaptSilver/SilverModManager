using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Controls.Spine;

/// <summary>Dragging a loadout icon in the spine reorders the loadouts through the view model.</summary>
[Collection(HeadlessUi.Collection)]
public class SpineReorderDragTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Rig(Window Window, App.UI.Controls.Spine.Spine Spine, List<(int From, int To)> Moves);

    private async Task<Rig> Show(int icons)
    {
        var moves = new List<(int From, int To)>();
        var items = new ReadOnlyObservableCollection<IImageButtonViewModel>(new ObservableCollection<IImageButtonViewModel>(
            Enumerable.Range(0, icons).Select(_ => Substitute.For<IImageButtonViewModel>())));
        var viewModel = Substitute.For<ISpineViewModel>();
        viewModel.LoadoutSpineItems.Returns(items);
        viewModel.When(vm => vm.MoveLoadout(Arg.Any<int>(), Arg.Any<int>())).Do(call => moves.Add((call.ArgAt<int>(0), call.ArgAt<int>(1))));

        return await OnUi(() =>
        {
            var spine = new App.UI.Controls.Spine.Spine { ViewModel = viewModel, Width = 120, Height = 600 };
            // The control defines these itself, so they are set on it rather than on the window: lookup finds the
            // control's own resource first.
            // The theme templates each icon through the view locator, which is not wired here; a plain square stands in.
            spine.FindControl<ItemsControl>("Loadouts")!.ItemTemplate = new FuncDataTemplate<IImageButtonViewModel>((_, _) => new Border { Width = 40, Height = 40, Background = Brushes.Gray });
            spine.Resources["SpineReorderSlideDuration"] = TimeSpan.Zero;
            spine.Resources["SpineReorderLiftDuration"] = TimeSpan.Zero;
            var window = Track(new Window { Width = 200, Height = 700, Content = spine });
            ShowRendered(window);
            Render(window);
            return Task.FromResult(new Rig(window, spine, moves));
        });
    }

    private static Control[] Icons(Rig rig) => rig.Spine.FindControl<ItemsControl>("Loadouts")!.ItemsPanelRoot!.Children.ToArray();

    [Fact]
    public async Task DraggingTheFirstIconBelowTheSecond_MovesTheLoadout()
    {
        var rig = await Show(3);

        await OnUi(() =>
        {
            var icons = Icons(rig);
            icons.Should().HaveCount(3);
            var from = ReorderDragGestures.CentreIn(icons[0], rig.Window);
            var to = ReorderDragGestures.CentreIn(icons[1], rig.Window) + new Vector(0, icons[1].Bounds.Height * 0.4);
            ReorderDragGestures.Drag(rig.Window, from, to);
        });

        await Eventually(() => rig.Moves.Should().Equal([(0, 1)]));
    }

    [Fact]
    public async Task EscapeDuringADrag_LeavesTheOrderAlone()
    {
        var rig = await Show(3);

        await OnUi(() =>
        {
            var icons = Icons(rig);
            var from = ReorderDragGestures.CentreIn(icons[0], rig.Window);
            var to = ReorderDragGestures.CentreIn(icons[1], rig.Window) + new Vector(0, icons[1].Bounds.Height * 0.4);
            ReorderDragGestures.PressAndMove(rig.Window, from, to);
            icons[0].RenderTransform.Should().NotBeNull("the icon is lifted while it is dragged");
            rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            rig.Window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });

        await Task.Delay(100);
        await OnUi(() =>
        {
            rig.Moves.Should().BeEmpty();
            Icons(rig).Should().OnlyContain(icon => icon.RenderTransform == null, "the drag was torn down");
        });
    }
}
