using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The per-mod rows: the running install's while the page follows it, the store's otherwise.</summary>
public partial class WabbajackModlistViewModelTests
{
    private async Task<(WabbajackModlistViewModel Vm, FakeRun Run, SilverModManager.MnemonicDB.Abstractions.EntityId Group, SilverModManager.Sdk.Loadouts.LoadoutId Loadout)> OpenWhileInstallingWithFolders(params string[] folders)
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        service.Running = new FakeRun();
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Installing into");
        vm.PinnedLoadout = loadout.LoadoutId;
        var group = await WabbajackGroupSeed.AddGroup(Connection, SomeFile.Value, loadout.LoadoutId, "Half written");
        foreach (var folder in folders) await WabbajackGroupSeed.AddModGroup(Connection, group, loadout.LoadoutId, folder);
        return (vm, service.Running, group, loadout.LoadoutId);
    }

    [Fact]
    public async Task FollowSwapsToTheRunsRows()
    {
        var (vm, run, _, _) = await OpenWhileInstallingWithFolders();
        vm.Mods.Should().BeSameAs(ModInstallRows.Empty, "nothing runs yet");

        using var activation = vm.Activator.Activate();

        await EventuallyOnUi(() => vm.Mods.Should().BeSameAs(run.Rows));
    }

    [Fact]
    public async Task OpenWhileInstallingShowsTheCurrentRowStatesAtOnce()
    {
        var (vm, run, _, _) = await OpenWhileInstallingWithFolders();
        run.Rows.Add([new ModInstallRow("A", "A", 0, new ModInstallRowState(ModInstallState.Installing, 3, 2, 5))]);

        using var activation = vm.Activator.Activate();

        await EventuallyOnUi(() => vm.Mods.Should().BeSameAs(run.Rows));
        var first = new List<ModInstallRowState>();
        using var _ = vm.Mods.Connect().Subscribe(set => first.AddRange(set.Select(change => change.Current.Current)));
        first.Should().ContainSingle().Which.State.Should().Be(ModInstallState.Installing);
    }

    [Fact]
    public async Task StopFollowingShowsTheStoreRows()
    {
        var (vm, run, _, _) = await OpenWhileInstallingWithFolders("A", "B");
        using var activation = vm.Activator.Activate();
        await EventuallyOnUi(() => vm.Mods.Should().BeSameAs(run.Rows));

        run.Done.SetResult();

        await EventuallyOnUi(() =>
        {
            vm.Mods.Should().NotBeSameAs(run.Rows);
            vm.Mods.Count.Should().Be(2);
        });
    }

    [Fact]
    public async Task ACancelledRunLeavesStoreRowsNotLiveOnes()
    {
        var (vm, run, _, _) = await OpenWhileInstallingWithFolders("A", "B", "C");
        using var activation = vm.Activator.Activate();
        await EventuallyOnUi(() => vm.Mods.Should().BeSameAs(run.Rows));
        run.Rows.Add([new ModInstallRow("A", "A", 0, new ModInstallRowState(ModInstallState.Installing, 1, 1, 2))]);

        run.Done.SetCanceled();

        await EventuallyOnUi(() =>
        {
            vm.Mods.Should().NotBeSameAs(run.Rows);
            vm.Mods.Count.Should().Be(3);
            vm.IsInstalling.Should().BeFalse();
        });
    }

    [Fact]
    public async Task ARevisionDuringTheRunLeavesTheLiveRowsAlone()
    {
        var (vm, run, group, loadout) = await OpenWhileInstallingWithFolders("A");
        using var activation = vm.Activator.Activate();
        await EventuallyOnUi(() => vm.Mods.Should().BeSameAs(run.Rows));

        await WabbajackGroupSeed.AddSkip(Connection, group, loadout);

        await EventuallyOnUi(() => vm.SkippedFiles.Should().HaveCount(1, "the revision was read"));
        vm.Mods.Should().BeSameAs(run.Rows);
    }

    [Fact]
    public async Task APageOpenedWithNoRunShowsTheStoreRowsAndKeepsThemWhileNothingChanges()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Finished list");
        vm.PinnedLoadout = loadout.LoadoutId;
        var group = await WabbajackGroupSeed.AddGroup(Connection, SomeFile.Value, loadout.LoadoutId, "Done");
        await WabbajackGroupSeed.AddModGroup(Connection, group, loadout.LoadoutId, "A");
        await vm.LoadAsync(CancellationToken.None);
        using var activation = vm.Activator.Activate();
        await EventuallyOnUi(() =>
        {
            vm.Mods.Count.Should().Be(1);
            vm.CanResume.Should().BeTrue("the group has no end written yet");
        });
        var shown = vm.Mods;

        await WabbajackGroupSeed.Finish(Connection, group);
        await EventuallyOnUi(() => vm.CanResume.Should().BeFalse("the revision that wrote the end was read"));

        vm.Mods.Should().BeSameAs(shown, "a revision that changed no row must not replace the list under the user's scroll position");
        await WabbajackGroupSeed.AddModGroup(Connection, group, loadout.LoadoutId, "B");
        await EventuallyOnUi(() => vm.Mods.Count.Should().Be(2));
    }

    [Fact]
    public async Task APageWithNoLoadoutPickedHasNoRows()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)));
        using var activation = vm.Activator.Activate();

        vm.Mods.Count.Should().Be(0);
    }
}
