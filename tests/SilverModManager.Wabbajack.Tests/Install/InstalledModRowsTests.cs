using System.Reactive.Linq;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Acquire;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>The per-mod rows the page shows once a run is over, rebuilt from what the install stored.</summary>
public class InstalledModRowsTests(ITestOutputHelper helper) : InstallTestBase<InstalledModRowsTests>(helper)
{
    private static readonly InstallOptions Defaults = new(false, new HashSet<RelativePath>());

    private async Task<(ModInstallRows Rows, WabbajackModlistLoadoutGroup.ReadOnly Group)> InstallAndRead(params string[] missing)
    {
        var archive = await AddZip("a.zip", new()
        {
            ["a.txt"] = "a"u8.ToArray(),
            ["b.txt"] = "b"u8.ToArray(),
            ["c.txt"] = "c"u8.ToArray(),
        });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(new FromArchive(P("mods/A/a.txt"), Xx64.Of("a"u8.ToArray()), 1, new ArchiveHashPath(archive.XxHash64, [P("a.txt")])));
        list.Directives.Add(new FromArchive(P("mods/A/b.txt"), Xx64.Of("b"u8.ToArray()), 1, new ArchiveHashPath(archive.XxHash64, [P("b.txt")])));
        list.Directives.Add(new FromArchive(P("mods/B/c.txt"), Xx64.Of("c"u8.ToArray()), 1, new ArchiveHashPath(archive.XxHash64, [P("c.txt")])));
        foreach (var to in missing)
            list.Directives.Add(new FromArchive(P(to), Xx64.From(1), 1, new ArchiveHashPath(archive.XxHash64, [P("nope.txt")])));
        var loadout = await CreateLoadout();

        var (_, group) = await Install(loadout, await AddModlist(list.ToList(), list.Blobs), Defaults);
        return (InstalledModRows.Read(Connection.Db, group.WabbajackModlistLoadoutGroupId), group);
    }

    private static List<ModInstallRow> RowsOf(ModInstallRows rows)
        => new[] { "A", "B" }.Select(key => rows.TryGet(key, out var row) ? row : null!).Where(row => row is not null).ToList();

    [Fact]
    public async Task ReadsOneInstalledRowPerModGroupWithItsFileCount()
    {
        var (rows, _) = await InstallAndRead();

        RowsOf(rows).Select(r => (r.Key, r.Name, r.Current.State, r.Current.Written, r.Current.Total))
            .Should().BeEquivalentTo([("A", "A", ModInstallState.Installed, 2, 2), ("B", "B", ModInstallState.Installed, 1, 1)]);
        RowsOf(rows).Select(r => r.Index).Should().BeEquivalentTo([0, 1]);
        rows.Totals.FirstAsync().Wait().Should().Be(new ModInstallTotals(2, 2, 0));
    }

    [Fact]
    public async Task ASkipUnderAFolderTurnsItsRowFailedWithThatReason()
    {
        var (rows, group) = await InstallAndRead("mods/B/missing.txt");

        var skip = SkipsOf(group).Should().ContainSingle().Subject;
        var shown = RowsOf(rows);
        shown.Select(r => r.Current.State).Should().Equal(ModInstallState.Installed, ModInstallState.Failed);
        shown[0].Current.Should().Match<ModInstallRowState>(s => s.Written == 2 && s.Total == 2);
        shown[1].Current.Reason.Should().Be(skip.Reason);
        rows.Totals.FirstAsync().Wait().Should().Be(new ModInstallTotals(2, 1, 1));
    }

    [Fact]
    public async Task ASkipUnderNoGroupCountsInTheFailedTotalOnly()
    {
        var (rows, _) = await InstallAndRead("mods/Ghost/missing.txt");

        RowsOf(rows).Should().HaveCount(2).And.OnlyContain(r => r.Current.State == ModInstallState.Installed);
        rows.Totals.FirstAsync().Wait().Should().Be(new ModInstallTotals(2, 2, 1));
    }

    private static ModInstallRows Made(ModInstallState state, string name = "A", string? reason = null)
    {
        var rows = new ModInstallRows();
        rows.Add([new ModInstallRow("A", name, 0, new ModInstallRowState(state, 1, 1, 1, reason))]);
        rows.SetTotals(new ModInstallTotals(1, state == ModInstallState.Installed ? 1 : 0, state == ModInstallState.Failed ? 1 : 0));
        return rows;
    }

    [Fact]
    public void TwoReadsOfTheSameStoreAreTheSameRows()
    {
        using var one = Made(ModInstallState.Failed, reason: "gone");
        using var two = Made(ModInstallState.Failed, reason: "gone");

        InstalledModRows.Same(one, two).Should().BeTrue("the page keeps its list, and its scroll position, when nothing changed");
    }

    [Fact]
    public void RowsThatDifferInStateNameOrTotalsAreNotTheSame()
    {
        using var installed = Made(ModInstallState.Installed);
        using var failed = Made(ModInstallState.Failed, reason: "gone");
        using var renamed = Made(ModInstallState.Installed, name: "Renamed");
        using var moreFailures = Made(ModInstallState.Installed);
        moreFailures.SetTotals(new ModInstallTotals(1, 1, 3));

        InstalledModRows.Same(installed, failed).Should().BeFalse();
        InstalledModRows.Same(installed, renamed).Should().BeFalse();
        InstalledModRows.Same(installed, moreFailures).Should().BeFalse("a skip that matches no folder shows in the header only");
    }
}
