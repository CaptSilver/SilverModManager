using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using FluentAssertions;
using NSubstitute;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.Platform.Gamepad;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

[Collection(HeadlessUi.Collection)]
public class SpineCycleTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>Stands in for <see cref="Spine"/>: the binding finds the spine through <see cref="IViewFor{T}"/>, which the real control implements.</summary>
    private sealed class SpineView : ContentControl, IViewFor<ISpineViewModel>
    {
        public ISpineViewModel? ViewModel { get; set; }

        object? IViewFor.ViewModel
        {
            get => ViewModel;
            set => ViewModel = (ISpineViewModel?)value;
        }
    }

    private static IImageButtonViewModel[] Items(int count, int active, int[] clicks)
    {
        return Enumerable.Range(0, count).Select(i =>
        {
            var item = Substitute.For<IImageButtonViewModel>();
            item.IsActive.Returns(i == active);
            item.Click.Returns(ReactiveCommand.Create(() => { clicks[i]++; }, outputScheduler: System.Reactive.Concurrency.ImmediateScheduler.Instance));
            return item;
        }).ToArray();
    }

    private static (SpineView View, Button Focus, int[] Clicks) Build(int count, int active)
    {
        var clicks = new int[count];
        var items = new ReadOnlyObservableCollection<IImageButtonViewModel>(new ObservableCollection<IImageButtonViewModel>(Items(count, active, clicks)));
        var spine = Substitute.For<ISpineViewModel>();
        spine.LoadoutSpineItems.Returns(items);
        var button = new Button { Content = "icon" };
        return (new SpineView { ViewModel = spine, Content = button }, button, clicks);
    }

    [Fact]
    public void TheRealSpineIsFoundThroughTheSameInterface()
    {
        typeof(Spine).Should().BeAssignableTo<IViewFor<ISpineViewModel>>();
    }

    [Fact]
    public async Task RbMovesToTheNextSpineLoadoutWhenTheSpineHasFocus()
    {
        await OnUi(() =>
        {
            var (view, focus, clicks) = Build(count: 3, active: 0);
            using var rig = new PadRig(view, () => [new SpineCycleBinding(forward: true), new SpineCycleBinding(forward: false)]);
            rig.Show();
            focus.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.RB));

            clicks.Should().Equal(0, 1, 0);
        });
    }

    [Fact]
    public async Task LbMovesToThePreviousSpineLoadout()
    {
        await OnUi(() =>
        {
            var (view, focus, clicks) = Build(count: 3, active: 2);
            using var rig = new PadRig(view, () => [new SpineCycleBinding(forward: true), new SpineCycleBinding(forward: false)]);
            rig.Show();
            focus.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.LB));

            clicks.Should().Equal(0, 1, 0);
        });
    }

    [Fact]
    public async Task RbOnTheLastLoadoutStays()
    {
        await OnUi(() =>
        {
            var (view, focus, clicks) = Build(count: 2, active: 1);
            var binding = new SpineCycleBinding(forward: true);
            using var rig = new PadRig(view, () => [binding]);
            rig.Show();
            focus.Focus(NavigationMethod.Tab);

            binding.CanExecute(rig.Navigator.CurrentContext()!).Should().BeFalse();
            clicks.Should().Equal(0, 0);
        });
    }

    [Fact]
    public async Task WithNoLoadoutActiveRbGoesToTheFirstOne()
    {
        await OnUi(() =>
        {
            var (view, focus, clicks) = Build(count: 2, active: -1);
            using var rig = new PadRig(view, () => [new SpineCycleBinding(forward: true)]);
            rig.Show();
            focus.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.RB));

            clicks.Should().Equal(1, 0);
        });
    }

    [Fact]
    public async Task OutsideTheSpineTheBindingStepsAside()
    {
        await OnUi(() =>
        {
            var binding = new SpineCycleBinding(forward: true);
            var other = new Button();
            using var rig = new PadRig(other, () => [binding]);
            rig.Show();
            other.Focus(NavigationMethod.Tab);

            binding.CanExecute(rig.Navigator.CurrentContext()!).Should().BeFalse();
        });
    }
}
