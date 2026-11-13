using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The list of ignored findings at the bottom of the Health Check page, and the button that brings one back.
/// </summary>
public class HealthCheckIgnoredFooterTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(DiagnosticListViewModel Vm, IDiagnosticManager Manager)> Create(LoadoutId loadout)
    {
        var manager = Substitute.For<IDiagnosticManager>();
        manager.GetLoadoutDiagnostics(Arg.Any<LoadoutId>()).Returns(new BehaviorSubject<Diagnostic[]>([]));
        manager.RefreshAsync(Arg.Any<LoadoutId>()).Returns(Task.CompletedTask);

        var vm = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<DiagnosticListViewModel>(Provider, manager, new FakeWindowManager())));
        await OnUi(() =>
        {
            vm.LoadoutId = loadout;
            vm.Activator.Activate();
            return Task.CompletedTask;
        });
        return (vm, manager);
    }

    private async Task<EntityId> Dismiss(LoadoutId loadout, string subject, string title)
    {
        using var tx = Connection.BeginTransaction();
        var row = new DiagnosticDismissal.New(tx)
        {
            LoadoutId = loadout,
            Source = "SilverModManager.Games.Generic",
            Number = 11,
            Subject = subject,
            Title = title,
        };
        return (await tx.Commit()).Remap(row).Id;
    }

    [Fact]
    public async Task ListsDismissalsOldestFirst()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Ignored Footer Lists");
        await Dismiss(loadout.LoadoutId, "Game|b.dll", "Code No Mod Provides");
        await Dismiss(loadout.LoadoutId, "Game|a.dll", "Loader Files");
        var (vm, _) = await Create(loadout.LoadoutId);

        await EventuallyOnUi(() => vm.IgnoredFindings.Select(f => (f.Title, f.Subject)).Should()
            .Equal(("Code No Mod Provides", "Game|b.dll"), ("Loader Files", "Game|a.dll")));
        vm.IgnoredFindings.Should().OnlyContain(f => f.When > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task AnotherLoadoutsDismissalsAreNotListed()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Ignored Footer Mine");
        var other = await SeedLoadoutRow(GameStore.Steam, "Ignored Footer Other");
        await Dismiss(other.LoadoutId, "Game|other.dll", "Loader Files");
        var (vm, _) = await Create(loadout.LoadoutId);

        await Task.Delay(100);
        await OnUi(() => vm.IgnoredFindings.Should().BeEmpty());
    }

    [Fact]
    public async Task ShowAgainDeletesTheRowAndRefreshes()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Ignored Footer Show Again");
        var row = await Dismiss(loadout.LoadoutId, "Game|a.dll", "Loader Files");
        var (vm, manager) = await Create(loadout.LoadoutId);
        await EventuallyOnUi(() => vm.IgnoredFindings.Should().ContainSingle());

        await OnUi(async () => await vm.ShowAgainCommand.Execute(row));

        DiagnosticDismissal.Load(Connection.Db, row).IsValid().Should().BeFalse();
        await EventuallyOnUi(() => vm.IgnoredFindings.Should().BeEmpty());
        await manager.Received().RefreshAsync(loadout.LoadoutId);
    }
}
