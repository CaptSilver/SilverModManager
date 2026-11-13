using DynamicData;
using FluentAssertions;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.Sdk.Tests.Loadouts.Install;

public class ModInstallRowsTests
{
    private static ModInstallRow RowOf(string key, int index = 0)
        => new(key, key, index, new ModInstallRowState(ModInstallState.Waiting, 0, 0, 2));

    [Fact]
    public void ConnectReplaysEveryRowToALateSubscriber()
    {
        using var rows = new ModInstallRows();
        rows.Add([RowOf("A", 0), RowOf("B", 1), RowOf("C", 2)]);

        var sets = new List<IChangeSet<ModInstallRow, string>>();
        using var subscription = rows.Connect().Subscribe(sets.Add);

        sets.Should().ContainSingle();
        sets[0].Should().HaveCount(3).And.OnlyContain(c => c.Reason == ChangeReason.Add);
        rows.Count.Should().Be(3);
    }

    [Fact]
    public void SetSwapsTheStateAndRaisesChangedOnce()
    {
        var row = RowOf("A");
        var seen = new List<ModInstallRowState>();
        using var subscription = row.Changed.Subscribe(seen.Add);

        row.Set(new ModInstallRowState(ModInstallState.Installing, 0, 1, 2));

        row.Current.Written.Should().Be(1);
        seen.Should().Equal(row.Current);
    }

    [Fact]
    public void DoneFollowsThePhase()
    {
        var extracting = new ModInstallRowState(ModInstallState.Extracting, 5, 0, 9);
        extracting.Done.Should().Be(5);
        (extracting with { State = ModInstallState.Installing, Written = 3 }).Done.Should().Be(3);
    }

    [Fact]
    public void SetTotalsRaisesOnlyWhenTheTotalsChange()
    {
        using var rows = new ModInstallRows();
        var seen = new List<ModInstallTotals>();
        using var subscription = rows.Totals.Subscribe(seen.Add);

        rows.SetTotals(new ModInstallTotals(3, 0, 0));
        rows.SetTotals(new ModInstallTotals(3, 0, 0));
        rows.SetTotals(new ModInstallTotals(3, 1, 0));

        seen.Should().Equal(new ModInstallTotals(0, 0, 0), new ModInstallTotals(3, 0, 0), new ModInstallTotals(3, 1, 0));
    }

    [Fact]
    public void TotalsReplaysTheLatestToALateSubscriber()
    {
        using var rows = new ModInstallRows();
        rows.SetTotals(new ModInstallTotals(7, 2, 1));

        ModInstallTotals? seen = null;
        using var subscription = rows.Totals.Subscribe(t => seen = t);

        seen.Should().Be(new ModInstallTotals(7, 2, 1));
    }

    [Fact]
    public void TryGetFindsARowByItsKey()
    {
        using var rows = new ModInstallRows();
        var row = RowOf("A");
        rows.Add([row]);

        rows.TryGet("A", out var found).Should().BeTrue();
        found.Should().BeSameAs(row);
        rows.TryGet("Z", out _).Should().BeFalse();
    }
}
