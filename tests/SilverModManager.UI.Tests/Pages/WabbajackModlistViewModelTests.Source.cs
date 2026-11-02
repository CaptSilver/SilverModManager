using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Install;
using SilverModManager.UI.Tests.Pages.Collections;

namespace SilverModManager.UI.Tests.Pages;

public partial class WabbajackModlistViewModelTests
{
    [Fact]
    public async Task ASourceWithoutALoadoutFollowsTheLoadoutThePickerHolds()
    {
        var (vm, _, _) = await Open(Plan());
        var picked = vm.SelectedLoadout!.Id!.Value;
        var file = await CollectionSeed.AddModlistFile(Connection, $"Picker list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, picked, file.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        var services = new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm);
        var source = new WabbajackModlistSource(services, loadoutId: null, file);
        source.CreateDownloads().Should().BeSameAs(vm);

        (await source.Group.FirstAsync()).Value.Value.Should().Be(group, "the picker starts on the first loadout of the game, which holds the install");
        (await source.IsInstalled.FirstAsync()).Should().BeTrue();

        await vm.SelectLoadoutAsync(WabbajackLoadoutChoice.CreateNew, CancellationToken.None);
        (await source.Group.FirstAsync()).HasValue.Should().BeFalse("a loadout that does not exist yet holds no install");
        (await source.StateLine.FirstAsync()).Should().Be("Not installed yet");
        (await source.CanToggle.FirstAsync()).Should().BeFalse();

        await vm.SelectLoadoutAsync(vm.LoadoutChoices.First(choice => choice.Id == picked), CancellationToken.None);
        (await source.Group.FirstAsync()).Value.Value.Should().Be(group);
    }

    [Fact]
    public async Task TheHeaderShowsTheSameProgressAsTheTabAndNeverAMixOfTwoSnapshots()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        var file = await CollectionSeed.AddModlistFile(Connection, $"Progress list {Guid.NewGuid():N}");
        var services = new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm);
        var source = new WabbajackModlistSource(services, loadoutId: null, file);
        source.CreateDownloads();
        var header = new List<CollectionInstallStatus>();
        using var subscription = source.InstallProgress.Subscribe(status => { if (status is not null) header.Add(status); });
        vm.InstallCommand.Execute().Subscribe();

        var snapshots = new[]
        {
            ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = 12, TotalItems = 40 },
            ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Resolve, DoneItems = 25871, TotalItems = 51774 },
            ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = 3, TotalItems = 10 },
        };
        foreach (var snapshot in snapshots)
        {
            service.Run.Snapshots.OnNext(snapshot);

            var shown = header[^1];
            shown.Text.Should().Be(WabbajackModlistText.Progress(snapshot));
            shown.Fraction.Should().BeApproximately(WabbajackModlistText.Fraction(snapshot), 1e-9);
            vm.Status.Text.Should().Be(shown.Text);
            vm.Status.Fraction.Should().Be(shown.Fraction, "the tab and the header read the same install progress");
        }

        var expected = snapshots.Select(snapshot => (WabbajackModlistText.Progress(snapshot), WabbajackModlistText.Fraction(snapshot))).ToHashSet();
        header.Should().OnlyContain(status => status.Text == "Checking the list" || expected.Any(pair => pair.Item1 == status.Text && Math.Abs(pair.Item2 - status.Fraction) < 1e-9),
            "an emission that pairs one snapshot's text with another's fraction would draw the wrong bar for a moment");
        service.Run.Done.SetResult();
    }
}
