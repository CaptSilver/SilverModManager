using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Games;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

// The page drawn in a headless window: what the user reads, not what the view model holds.
public partial class WabbajackModlistViewModelTests
{
    private async Task<WabbajackModlistView> ShowView(WabbajackModlistViewModel vm)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new WabbajackModlistView { ViewModel = vm };
            var window = Track(new Window { Width = 1000, Height = 800, Content = view });
            ShowRendered(window);
            return view;
        });
    }

    private static string[] TextsIn(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();

    [Fact]
    public async Task TheLoadoutPicker_ShowsTheNameOfTheNewLoadoutEntry_NotTheRecord()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)), game: GameId.From("CreationEngine.NoSuchGame"));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            var box = view.FindControl<ComboBox>("LoadoutBox")!;
            Render((Window)view.GetVisualRoot()!);
            var texts = TextsIn(box);
            texts.Should().Contain("Create new loadout");
            texts.Should().NotContain(text => text.Contains("WabbajackLoadoutChoice"));
        });
    }

    [Fact]
    public async Task TheLoadoutPicker_ShowsTheNameOfTheSelectedExistingLoadout()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            var box = view.FindControl<ComboBox>("LoadoutBox")!;
            Render((Window)view.GetVisualRoot()!);
            var name = vm.SelectedLoadout!.Name;
            name.Should().NotBeEmpty();
            TextsIn(box).Should().Contain(name);
        });
    }

    [Fact]
    public async Task ADisabledButton_KeepsItsReasonInItsTooltip_AndThePageSaysTheBlockingOneOnce()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, "a.7z"), Row(ArchiveState.Have, "b.7z")));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var install = view.FindControl<Button>("InstallButton")!;
            var download = view.FindControl<Button>("DownloadButton")!;
            var nextFive = view.FindControl<Button>("OpenNextFiveButton")!;
            install.IsEffectivelyEnabled.Should().BeFalse();
            download.IsEffectivelyEnabled.Should().BeFalse();
            nextFive.IsEffectivelyEnabled.Should().BeFalse();

            ToolTip.GetTip(install).Should().Be(vm.InstallBlockedReason);
            vm.InstallBlockedReason.Should().Contain("1 of 2 archives is not in your library yet");
            ToolTip.GetShowOnDisabled(install).Should().BeTrue();

            ToolTip.GetTip(download).Should().Be(vm.DownloadBlockedReason);
            vm.DownloadBlockedReason.Should().Contain("The missing archive cannot be fetched by the app");

            vm.OpenNextFiveBlockedReason.Should().Be(vm.DownloadBlockedReason, "Download cannot be pressed, so pointing at it would help no one");
            ToolTip.GetTip(nextFive).Should().Be(vm.OpenNextFiveBlockedReason);
            ToolTip.GetShowOnDisabled(nextFive).Should().BeTrue();

            // Install's reason names the next step, so it is the one the page shows, and only there.
            view.FindControl<TextBlock>("StatusText")!.Text.Should().Be(vm.InstallBlockedReason);
            TextsIn(view).Count(text => text == vm.InstallBlockedReason).Should().Be(1);
            TextsIn(view).Should().NotContain(vm.DownloadBlockedReason);
        });
    }

    [Fact]
    public async Task AnEnabledButton_HasNoReasonToShow()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have, "b.7z")));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var install = view.FindControl<Button>("InstallButton")!;
            install.IsEffectivelyEnabled.Should().BeTrue();
            ToolTip.GetTip(install).Should().BeNull();
        });
    }

    [Fact]
    public async Task WhileTheListIsChecked_TheViewShowsOneProgressLineWithABar_AndNoReasonSentence()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        // Showing the page is what starts loading the list.
        var view = await ShowView(vm);
        await UntilPlanning(service);

        // No Render tick while the bar is indeterminate: its endless animation never lets the headless tick finish.
        await EventuallyOnUi(() =>
        {
            var line = view.FindControl<TextBlock>("StatusText")!;
            var bar = view.FindControl<ProgressBar>("StatusBar")!;
            line.IsVisible.Should().BeTrue();
            line.Text.Should().Be(vm.CheckText).And.NotBeEmpty();
            bar.IsVisible.Should().BeTrue();
            bar.IsIndeterminate.Should().BeTrue("nothing has been counted yet");
            vm.DownloadBlockedReason.Should().Contain("Checking the list");
            TextsIn(view).Should().NotContain(text => text.Contains("Checking the list against"), "the buttons keep that reason in their tooltips");
        });

        service.LastProgress!.Report(new SilverModManager.Wabbajack.Acquire.PlanProgress(SilverModManager.Wabbajack.Acquire.PlanPhase.CheckingLibrary, 412, 1294));
        await EventuallyOnUi(() =>
        {
            view.FindControl<TextBlock>("StatusText")!.Text.Should().Be("Checking archives against your library: 412 of 1294");
            var bar = view.FindControl<ProgressBar>("StatusBar")!;
            bar.IsIndeterminate.Should().BeFalse();
            bar.Value.Should().BeApproximately(412d / 1294, 1e-6);
        });

        gate.SetResult();
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            vm.IsPlanning.Should().BeFalse();
            view.FindControl<TextBlock>("StatusText")!.Text.Should().NotContain("Checking archives");
            view.FindControl<ProgressBar>("StatusBar")!.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task WhileTheListIsChecked_TheLoadoutPickerShowsTheChoiceNameInItsSelectionBox()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), game: GameId.From("CreationEngine.NoSuchGame"), load: false);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        var view = await ShowView(vm);
        await UntilPlanning(service);

        await EventuallyOnUi(() =>
        {
            var box = view.FindControl<ComboBox>("LoadoutBox")!;
            ((Window)view.GetVisualRoot()!).UpdateLayout();
            box.SelectedItem.Should().Be(WabbajackLoadoutChoice.CreateNew);
            var shown = box.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
            shown.Should().Contain("Create new loadout");
            shown.Should().NotContain(text => text.Contains("WabbajackLoadoutChoice") || text.Contains("Id ="));
        });

        gate.SetResult();
        await EventuallyOnUi(() => vm.IsPlanning.Should().BeFalse());
    }
}
