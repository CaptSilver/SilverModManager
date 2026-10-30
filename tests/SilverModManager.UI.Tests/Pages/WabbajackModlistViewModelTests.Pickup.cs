using System.Reactive.Linq;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

// The Downloads pickup on the modlist page: the switch, the folder, and the hint on rows that wait for a browser download.
public partial class WabbajackModlistViewModelTests
{
    private const string PickUpHint = "Download it in your browser; it is picked up from Downloads automatically";

    [Theory]
    [InlineData(ArchiveState.Browser, true)]
    [InlineData(ArchiveState.Manual, true)]
    [InlineData(ArchiveState.Have, false)]
    [InlineData(ArchiveState.Automatic, false)]
    [InlineData(ArchiveState.GameFile, false)]
    [InlineData(ArchiveState.Unsupported, false)]
    [InlineData(ArchiveState.NotNeeded, false)]
    public async Task WithThePickupOn_OnlyRowsWaitingForABrowserDownloadCarryTheHint(ArchiveState state, bool hinted)
    {
        var (vm, _, _) = await Open(Plan(Row(state)), pickUp: true);

        if (hinted) vm.Archives[0].Reason.Should().EndWith(PickUpHint);
        else vm.Archives[0].Reason.Should().NotContain(PickUpHint);
    }

    [Fact]
    public async Task WithThePickupOff_NoRowCarriesTheHint()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, "a.7z"), Row(ArchiveState.Browser, "b.7z")), pickUp: false);

        vm.Archives.Should().OnlyContain(row => !row.Reason.Contains(PickUpHint));
    }

    [Fact]
    public async Task TheHint_FollowsTheRowsOwnReasonAfterASemicolon()
    {
        var manual = Row(ArchiveState.Manual) with { Reason = "get it from the forum" };
        var (vm, _, _) = await Open(Plan(manual), pickUp: true);

        vm.Archives[0].Reason.Should().Be($"get it from the forum; {PickUpHint}");
    }

    [Fact]
    public async Task TogglingThePickup_TellsTheSameRowsTheirReasonChangedAndWritesTheSetting()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual, "a.7z"), Row(ArchiveState.Have, "b.7z")), pickUp: false);
        var list = vm.Archives;
        var rows = vm.Archives.ToArray();
        var seen = rows.Select(_ => new List<string?>()).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var changes = seen[i];
            ((INotifyPropertyChanged)rows[i]).PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        }

        vm.PickUpFromDownloads = true;

        vm.Archives.Should().BeSameAs(list, "the table is not rebuilt");
        vm.Archives[0].Should().BeSameAs(rows[0]);
        vm.Archives[0].Reason.Should().EndWith(PickUpHint);
        seen[0].Should().Equal(nameof(WabbajackArchiveRow.Reason));
        seen[1].Should().BeEmpty("a held archive has no hint to show or hide");
        service.PickUpFromDownloads.Should().BeTrue();

        vm.PickUpFromDownloads = false;

        vm.Archives[0].Reason.Should().NotContain(PickUpHint);
        service.PickUpFromDownloads.Should().BeFalse();
    }

    [Fact]
    public async Task AnotherSetOfArchives_GetsNewRowsThatCarryTheCurrentToggle()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "a.7z")), pickUp: false);
        using var activation = vm.Activator.Activate();
        vm.PickUpFromDownloads = true;

        acquirer.Change(Plan(Row(ArchiveState.Manual, "other.7z")) with { Modlist = SomeFile });

        vm.Archives.Should().ContainSingle().Which.Reason.Should().EndWith(PickUpHint);
    }

    [Fact]
    public async Task ARowChangingState_GainsAndLosesTheHintInPlace()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "a.7z")), pickUp: true);
        using var activation = vm.Activator.Activate();
        var row = vm.Archives[0];
        var changes = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        acquirer.Change(Plan(Row(ArchiveState.Have, "a.7z")));
        row.Reason.Should().NotContain(PickUpHint);
        changes.Should().Contain(nameof(WabbajackArchiveRow.Reason));

        changes.Clear();
        acquirer.Change(Plan(Row(ArchiveState.Manual, "a.7z")));
        // A burst of announcements is shown a moment after the first.
        await EventuallyOnUi(() => row.Reason.Should().EndWith(PickUpHint));
        vm.Archives[0].Should().BeSameAs(row);
        changes.Should().Contain(nameof(WabbajackArchiveRow.Reason));
    }

    [Fact]
    public async Task TheSwitchIsOffered_OnlyWhileSomeArchiveWaitsForABrowserDownload()
    {
        var (waiting, _, _) = await Open(Plan(Row(ArchiveState.Have, "a.7z"), Row(ArchiveState.Browser, "b.7z")));
        var (done, _, _) = await Open(Plan(Row(ArchiveState.Have, "a.7z"), Row(ArchiveState.Automatic, "b.7z")));

        waiting.HasWaitingArchives.Should().BeTrue();
        done.HasWaitingArchives.Should().BeFalse();
    }

    [Fact]
    public async Task TheTooltip_NamesTheFolderBeingWatched_OrSaysThePickupIsOff()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual)), pickUp: false);
        vm.PickUpTooltip.Should().Be("Pickup is off");

        service.IsWatchingDownloads = true;
        vm.PickUpFromDownloads = true;

        vm.PickUpTooltip.Should().Be("Watching /home/test/Downloads");
    }

    [Fact]
    public async Task TheTooltip_SaysWhenTheFolderCannotBeWatched()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual)), pickUp: false);
        service.IsWatchingDownloads = false;

        vm.PickUpFromDownloads = true;

        vm.PickUpTooltip.Should().Be("Not watching: /home/test/Downloads is not a folder that exists");
    }

    [Fact]
    public async Task ChoosingADownloadFolder_GoesThroughTheServiceAndRefreshesTheTooltip()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual)), pickUp: true);
        service.IsWatchingDownloads = true;

        await vm.ChooseDownloadsFolderCommand.Execute();

        service.FolderChoices.Should().Be(1);
        vm.PickUpTooltip.Should().Be("Watching /mnt/chosen");

        await vm.UseDefaultDownloadsFolderCommand.Execute();

        service.FolderResets.Should().Be(1);
        vm.PickUpTooltip.Should().Be("Watching /home/test/Downloads");
    }

    [Fact]
    public async Task ARealizedLine_ShowsTheHintAfterTheToggle_WithoutBeingRebuilt()
    {
        var plan = BigPlan(3, ArchiveState.Manual);
        var (vm, _, _) = await Open(plan, pickUp: false);
        using var activation = vm.Activator.Activate();
        var view = await ShowView(vm);
        Control[] before = [];
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            before = Lines(view).ToArray();
            before.Should().NotBeEmpty();
        });
        await EventuallyOnUi(() => TextsIn(before[0]).Should().NotContain(text => text.Contains("picked up from Downloads")));

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => vm.PickUpFromDownloads = true);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var after = Lines(view).ToArray();
            // The panel may swap the spare element at the end when row heights change; the lines of the changed rows stay.
            after.Take(2).Should().Equal(before.Take(2), "the toggle must not rebuild the lines of the rows it touches");
            TextsIn(before[0]).Should().Contain(text => text.Contains("picked up from Downloads automatically"));
        });
    }

    [Fact]
    public async Task TheCheckbox_ShowsOnlyWhileAnArchiveWaiting_AndFollowsTheToggle()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "a.7z")));
        using var activation = vm.Activator.Activate();
        var view = await ShowView(vm);
        CheckBox Box() => view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PickUpBox");

        await EventuallyOnUi(() => Box().IsVisible.Should().BeTrue());
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Box().IsChecked = true);
        vm.PickUpFromDownloads.Should().BeTrue();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => acquirer.Change(Plan(Row(ArchiveState.Have, "a.7z"))));

        await EventuallyOnUi(() => Box().IsVisible.Should().BeFalse());
    }
}
