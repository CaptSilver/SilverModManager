using System.Reactive;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NSubstitute;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.Input;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Input.Menu;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Gamepad;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

[Collection(HeadlessUi.Collection)]
public class AppMenuTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Calls
    {
        public int Settings;
        public int Apply;
        public int Play;
    }

    private static (ITopBarViewModel TopBar, ILoadoutLeftMenuViewModel Left, Calls Calls) Fixtures()
    {
        var calls = new Calls();
        var topBar = Substitute.For<ITopBarViewModel>();
        topBar.OpenSettingsCommand.Returns(ReactiveCommand.Create<NavigationInformation>(_ => calls.Settings++, outputScheduler: System.Reactive.Concurrency.ImmediateScheduler.Instance));
        var launch = Substitute.For<ILaunchButtonViewModel>();
        launch.Command.Returns(ReactiveCommand.Create(() => { calls.Play++; }, outputScheduler: System.Reactive.Concurrency.ImmediateScheduler.Instance));
        var apply = Substitute.For<IApplyControlViewModel>();
        apply.ApplyCommand.Returns(ReactiveCommand.Create(() => { calls.Apply++; }, outputScheduler: System.Reactive.Concurrency.ImmediateScheduler.Instance));
        apply.LaunchButtonViewModel.Returns(launch);
        var left = Substitute.For<ILoadoutLeftMenuViewModel>();
        left.ApplyControlViewModel.Returns(apply);
        return (topBar, left, calls);
    }

    [Fact]
    public void TheMenuListsSettingsApplyAndPlay()
    {
        var (topBar, left, _) = Fixtures();

        AppMenuItems.Build(topBar, left).Select(i => i.Title).Should().Equal("Settings", "Apply", "Play");
    }

    [Fact]
    public void EachEntryRunsItsCommand()
    {
        var (topBar, left, calls) = Fixtures();
        var items = AppMenuItems.Build(topBar, left);

        foreach (var item in items) item.Run();

        (calls.Settings, calls.Apply, calls.Play).Should().Be((1, 1, 1));
    }

    [Fact]
    public void WithoutALoadoutApplyAndPlayAreDisabledWithAReason()
    {
        var (topBar, _, _) = Fixtures();

        var items = AppMenuItems.Build(topBar, leftMenu: null);

        items[0].IsEnabled.Should().BeTrue();
        items[1].DisabledReason.Should().Be(AppMenuItems.NoLoadoutReason);
        items[2].DisabledReason.Should().Be(AppMenuItems.NoLoadoutReason);
    }

    [Fact]
    public void ChoosingAnEntryClosesTheMenuThenRunsIt()
    {
        var order = new List<string>();
        var overlays = Substitute.For<IOverlayController>();
        overlays.When(o => o.Remove(Arg.Any<IOverlayViewModel>())).Do(_ => order.Add("closed"));
        var menu = new AppMenuOverlayViewModel([new AppMenuItem("Go", () => order.Add("ran"))]) { Controller = overlays };

        menu.Choose(menu.Items[0]);

        order.Should().Equal("closed", "ran");
    }

    [Fact]
    public void ChoosingADisabledEntryDoesNothing()
    {
        var ran = false;
        var overlays = Substitute.For<IOverlayController>();
        var menu = new AppMenuOverlayViewModel([new AppMenuItem("Go", () => ran = true, "no")]) { Controller = overlays };

        menu.Choose(menu.Items[0]);

        ran.Should().BeFalse();
        overlays.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [Fact]
    public async Task MenuOpensTheAppMenuOverlay()
    {
        await OnUi(() =>
        {
            var (topBar, left, _) = Fixtures();
            var binding = new MenuBinding(_ => (topBar, left));
            using var rig = new PadRig(new Button(), () => [binding]);
            rig.Show();

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.Menu));

            rig.Overlays.Received(1).Enqueue(Arg.Is<IOverlayViewModel>(o => o is IAppMenuOverlayViewModel));
        });
    }

    [Fact]
    public async Task MenuDoesNotStackASecondMenu()
    {
        await OnUi(() =>
        {
            var (topBar, left, _) = Fixtures();
            var binding = new MenuBinding(_ => (topBar, left));
            using var rig = new PadRig(new Button(), () => [binding]);
            rig.Overlays.CurrentOverlay.Returns(Substitute.For<IAppMenuOverlayViewModel>());
            rig.Show();

            binding.CanExecute(rig.Navigator.CurrentContext()!).Should().BeFalse();
        });
    }

    [Fact]
    public async Task TheMenuViewFocusesItsFirstEntry()
    {
        await OnUi(() =>
        {
            var (topBar, left, _) = Fixtures();
            var menu = new AppMenuOverlayViewModel(AppMenuItems.Build(topBar, left)) { Controller = Substitute.For<IOverlayController>() };
            var view = new AppMenuOverlayView { ViewModel = menu, DataContext = menu };
            var window = Track(new Window { Content = view, Width = 500, Height = 400 });
            ShowRendered(window);
            Dispatcher.UIThread.RunJobs();

            var first = view.GetVisualDescendants().OfType<Button>().First();
            first.IsFocused.Should().BeTrue();
            TextsOf(first).Should().Equal("Settings");
        });
    }

    [Fact]
    public async Task ADisabledEntryShowsItsReasonInTheMenu()
    {
        await OnUi(() =>
        {
            var (topBar, _, _) = Fixtures();
            var menu = new AppMenuOverlayViewModel(AppMenuItems.Build(topBar, leftMenu: null)) { Controller = Substitute.For<IOverlayController>() };
            var view = new AppMenuOverlayView { ViewModel = menu, DataContext = menu };
            var window = Track(new Window { Content = view, Width = 500, Height = 400 });
            ShowRendered(window);
            Dispatcher.UIThread.RunJobs();

            // A pad can neither hover for a tooltip nor focus a disabled button, so the reason has to be on screen.
            var apply = view.GetVisualDescendants().OfType<Button>().ElementAt(1);
            apply.IsEnabled.Should().BeFalse();
            TextsOf(apply).Should().Equal("Apply", AppMenuItems.NoLoadoutReason);
        });
    }

    private static IEnumerable<string?> TextsOf(Button button) =>
        button.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);
}
