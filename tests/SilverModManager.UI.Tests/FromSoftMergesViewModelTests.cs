using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.FromSoftMerges;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using NSubstitute;
using ReactiveUI;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class FromSoftMergesViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static MergeReportEntry Entry(string path, string strategy, string[] contributors, MergeReportOutcome outcome = MergeReportOutcome.Merged,
        string[]? notes = null, string vanilla = "", string reason = "")
        => new(path, strategy, contributors, outcome, notes ?? [], vanilla, reason);

    private static MergeReport Report(string? refusal = null, params MergeReportEntry[] entries)
        => new(entries, refusal, MergeReport.DefaultPriorityOrderHint);

    private FromSoftMergesViewModel CreateVm(IMergeReports reports)
        => new(Substitute.For<IWindowManager>(), reports, Provider.GetRequiredService<IConnection>(), NullLogger<FromSoftMergesViewModel>.Instance);

    private static IMergeReports Reports(params MergeReport[] reports)
    {
        var source = Substitute.For<IMergeReports>();
        var calls = 0;
        source.ForLoadout(Arg.Any<SilverModManager.Sdk.Loadouts.LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(reports[Math.Min(Interlocked.Increment(ref calls) - 1, reports.Length - 1)]));
        return source;
    }

    private static void Refresh(FromSoftMergesViewModel vm) => vm.RefreshCommand.Execute().Subscribe();

    // The rows appear before the refresh restores the selection; a test that selects a row must wait for the refresh to end.
    private async Task Loaded(FromSoftMergesViewModel vm, int rows) => await Eventually(() =>
    {
        vm.Entries.Should().HaveCount(rows);
        vm.IsLoading.Should().BeFalse();
    });

    [Fact]
    public async Task EntriesKeepTheReportsOrderAndStrategySpelling()
    {
        var vm = CreateVm(Reports(Report(null,
            Entry("regulation.bin", "param-rows", ["High", "Low"]),
            Entry("msg/engus/item.msgbnd.dcx", "fmg-union", ["Low", "High"]))));
        Refresh(vm);

        await Eventually(() => vm.Entries.Should().HaveCount(2));
        vm.Entries.Select(e => (e.Path, e.Strategy)).Should().Equal(("regulation.bin", "param-rows"), ("msg/engus/item.msgbnd.dcx", "fmg-union"));
        vm.Entries[0].Contributors.Should().Equal("High", "Low");
        vm.Entries[1].Contributors.Should().Equal("Low", "High");
        vm.PriorityOrderHint.Should().Be(MergeReport.DefaultPriorityOrderHint);
        vm.EmptyState.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusalShowsTheBannerAndTheEntrysOutcome()
    {
        var vm = CreateVm(Reports(Report("The apply is refused: regulation.bin could not be merged",
            Entry("regulation.bin", "param-rows", ["A", "B"], MergeReportOutcome.Refused, reason: "no vanilla source"))));
        Refresh(vm);

        await Eventually(() => vm.Entries.Should().HaveCount(1));
        vm.Refusal.Should().Be("The apply is refused: regulation.bin could not be merged");
        vm.Entries[0].Outcome.Should().Be(MergeReportOutcome.Refused);
    }

    [Fact]
    public async Task SelectingAnEntryExposesItsNotes()
    {
        var vm = CreateVm(Reports(Report(null,
            Entry("regulation.bin", "param-rows", ["A", "B"], notes: ["Row 12 of EquipParamGoods: A wins over B"], vanilla: "vanilla dump"))));
        Refresh(vm);
        await Loaded(vm, rows: 1);

        vm.SelectedNotes.Should().BeEmpty();
        vm.Selected = vm.Entries[0];

        vm.SelectedNotes.Should().Equal("Row 12 of EquipParamGoods: A wins over B");
    }

    [Fact]
    public async Task TheSelectedPathSurvivesARefreshThatReplacesTheRows()
    {
        var vm = CreateVm(Reports(
            Report(null, Entry("a.bin", "param-rows", ["A", "B"]), Entry("regulation.bin", "param-rows", ["A", "B"], notes: ["first"])),
            Report(null, Entry("regulation.bin", "param-rows", ["B", "A"], notes: ["second"]))));
        // The list view clears its selection whenever its rows are replaced, before the refresh restores it.
        vm.WhenAnyValue(x => x.Entries).Subscribe(_ => vm.Selected = null);
        Refresh(vm);
        await Loaded(vm, rows: 2);
        vm.Selected = vm.Entries[1];

        Refresh(vm);

        await Eventually(() =>
        {
            vm.Selected?.Path.Should().Be("regulation.bin");
            vm.Selected.Should().NotBeNull();
            vm.SelectedNotes.Should().Equal("second");
        });
    }

    [Fact]
    public async Task AnExceptionFromTheSourceLandsInProblem()
    {
        var source = Substitute.For<IMergeReports>();
        source.ForLoadout(Arg.Any<SilverModManager.Sdk.Loadouts.LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns<Task<MergeReport>>(_ => throw new InvalidOperationException("index is broken"));
        var vm = CreateVm(source);
        Refresh(vm);

        await Eventually(() => vm.Problem.Should().Contain("index is broken"));
        vm.Entries.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task AnEmptyReportShowsTheEmptyStateText()
    {
        var vm = CreateVm(Reports(Report()));
        Refresh(vm);

        await Eventually(() => vm.EmptyState.Should().Be("No file is provided by more than one enabled mod"));
        vm.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ARevisionChangeReplacesTheRows()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "merges-vm");
        // The stream may refresh more than once on subscribe, so the report depends on the state, not on the call count.
        var current = Report(null, Entry("a.bin", "param-rows", ["A", "B"]));
        var source = Substitute.For<IMergeReports>();
        source.ForLoadout(Arg.Any<SilverModManager.Sdk.Loadouts.LoadoutId>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Volatile.Read(ref current)));
        var vm = CreateVm(source);

        using var _ = vm.Activator.Activate();
        vm.LoadoutId = loadout.LoadoutId;
        await Eventually(() => vm.Entries.Select(e => e.Path).Should().Equal("a.bin"));
        Volatile.Write(ref current, Report(null, Entry("b.bin", "fmg-union", ["B", "A"])));

        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.Name, "merges-vm-renamed");
        await tx.Commit();

        await Eventually(() => vm.Entries.Select(e => e.Path).Should().Equal("b.bin"));
    }
}
