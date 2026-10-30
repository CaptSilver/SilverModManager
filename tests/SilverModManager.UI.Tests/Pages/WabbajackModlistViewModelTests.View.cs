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
    public async Task ADisabledButton_ShowsItsReasonUnderneathAndInItsTooltip()
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

            var installReason = view.FindControl<TextBlock>("InstallReasonText")!;
            installReason.IsVisible.Should().BeTrue();
            installReason.Text.Should().Contain("1 of 2 archives is not in your library yet");
            ToolTip.GetTip(install).Should().Be(installReason.Text);
            ToolTip.GetShowOnDisabled(install).Should().BeTrue();

            var downloadReason = view.FindControl<TextBlock>("DownloadReasonText")!;
            downloadReason.IsVisible.Should().BeTrue();
            downloadReason.Text.Should().Contain("The missing archive cannot be fetched by the app");
            ToolTip.GetTip(download).Should().Be(downloadReason.Text);

            var nextFiveReason = view.FindControl<TextBlock>("OpenNextFiveReasonText")!;
            nextFiveReason.Text.Should().Be(downloadReason.Text, "Download cannot be pressed, so pointing at it would help no one");
            ToolTip.GetTip(nextFive).Should().Be(nextFiveReason.Text);
            ToolTip.GetShowOnDisabled(nextFive).Should().BeTrue();
        });
    }

    [Fact]
    public async Task AnEnabledButton_ShowsNoReason()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have, "b.7z")));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            view.FindControl<Button>("InstallButton")!.IsEffectivelyEnabled.Should().BeTrue();
            view.FindControl<TextBlock>("InstallReasonText")!.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task WhileTheListIsChecked_TheViewShowsTheReasonAndAProgressLineWithABar()
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
            var line = view.FindControl<TextBlock>("CheckText")!;
            var bar = view.FindControl<ProgressBar>("CheckBar")!;
            line.IsVisible.Should().BeTrue();
            line.Text.Should().NotBeEmpty();
            bar.IsVisible.Should().BeTrue();
            bar.IsIndeterminate.Should().BeTrue("nothing has been counted yet");
            view.FindControl<TextBlock>("DownloadReasonText")!.IsVisible.Should().BeTrue();
            view.FindControl<TextBlock>("DownloadReasonText")!.Text.Should().Contain("Checking the list");
            view.FindControl<TextBlock>("InstallReasonText")!.IsVisible.Should().BeTrue();
        });

        service.LastProgress!.Report(new SilverModManager.Wabbajack.Acquire.PlanProgress(SilverModManager.Wabbajack.Acquire.PlanPhase.CheckingLibrary, 412, 1294));
        await EventuallyOnUi(() =>
        {
            view.FindControl<TextBlock>("CheckText")!.Text.Should().Be("Checking archives against your library: 412 of 1294");
            var bar = view.FindControl<ProgressBar>("CheckBar")!;
            bar.IsIndeterminate.Should().BeFalse();
            bar.Value.Should().BeApproximately(412d / 1294, 1e-6);
        });

        gate.SetResult();
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            vm.IsPlanning.Should().BeFalse();
            view.FindControl<TextBlock>("CheckText")!.IsVisible.Should().BeFalse();
            view.FindControl<ProgressBar>("CheckBar")!.IsVisible.Should().BeFalse();
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
