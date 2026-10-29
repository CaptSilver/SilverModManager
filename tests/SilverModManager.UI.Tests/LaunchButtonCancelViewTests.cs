using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests;

public class LaunchButtonCancelViewTests(IServiceProvider provider) : AUiTest(provider)
{
    // The state the left menu is in while a launch waits for Steam: the run-game job is active, so the apply
    // control turns launching off, and the launch button shows Cancel.
    private sealed class WaitingApplyControl : AViewModel<IApplyControlViewModel>, IApplyControlViewModel
    {
        public WaitingApplyControl(bool canLaunch)
        {
            LaunchButtonViewModel = new LaunchButtonDesignViewModel { IsWaitingForSteam = true, CanLaunch = canLaunch };
        }

        public ReactiveCommand<Unit, Unit> ApplyCommand { get; } = ReactiveCommand.Create(() => { }, Observable.Return(false));
        public ReactiveCommand<NavigationInformation, Unit> ShowApplyDiffCommand { get; } = ReactiveCommand.Create<NavigationInformation>(_ => { });
        public ILaunchButtonViewModel LaunchButtonViewModel { get; }
        public bool IsProcessing => false;
        public bool IsApplying => false;
        public string ApplyButtonText => "APPLY";
        public string ProcessingText => "";
        public double ProgressFraction => 0;
        public bool IsProgressIndeterminate => true;
    }

    private async Task<(Button Launch, Button Cancel)> Show(bool canLaunch)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new ApplyControlView { ViewModel = new WaitingApplyControl(canLaunch) };
            var window = Track(new Window { Width = 300, Height = 300, Content = view });
            ShowRendered(window);
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            return (buttons.Single(b => b.Name == "LaunchButton"), buttons.Single(b => b.Name == "CancelButton"));
        });
    }

    [Fact]
    public async Task WhileWaitingForSteam_CancelStaysClickableWhenLaunchingIsOff()
    {
        var (launch, cancel) = await Show(canLaunch: false);
        await OnUi(() =>
        {
            cancel.IsVisible.Should().BeTrue();
            cancel.Command!.CanExecute(null).Should().BeTrue("the cancel command itself is available");
            cancel.IsEffectivelyEnabled.Should().BeTrue("Cancel is the way out of a Steam wait, so turning launching off must not grey it");
            launch.IsEffectivelyEnabled.Should().BeFalse();
        });
    }

    [Fact]
    public async Task WhileWaitingForSteam_CancelIsClickableWhenLaunchingIsOn()
    {
        var (_, cancel) = await Show(canLaunch: true);
        await OnUi(() =>
        {
            cancel.IsVisible.Should().BeTrue();
            cancel.IsEffectivelyEnabled.Should().BeTrue();
        });
    }
}
