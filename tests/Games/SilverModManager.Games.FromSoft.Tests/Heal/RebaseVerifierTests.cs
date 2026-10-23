using FluentAssertions;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Tests.Build;
using NexusMods.Games.FromSoft.Tests.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Heal;

public class RebaseVerifierTests
{
    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private const string Installed = BuildIds.RegulationVersion;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows(params (string Table, (int Id, string Data)[] Rows)[] tables)
        => tables.ToDictionary(
            t => t.Table,
            t => (IReadOnlyDictionary<int, byte[]>)t.Rows.ToDictionary(r => r.Id, r => System.Text.Encoding.ASCII.GetBytes(r.Data)));

    private static IReadOnlyList<string> Check(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> merged,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> baseline,
        string mergedVersion = Installed,
        params (string Mod, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows)[] mods)
        => RebaseVerifier.Check(mergedVersion, merged, baseline, mods, Installed);

    [Fact]
    public void PassesWhenEveryAuthoredRowSurvived()
    {
        var problems = Check(
            Rows(("A.param", [(1, "MOD"), (2, "v"), (3, "new")])),
            Rows(("A.param", [(1, "v"), (2, "v")])),
            mods: ("m", Rows(("A.param", [(1, "MOD"), (2, "v")]))));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void CatchesAnAuthoredRowLostInTheRebase()
    {
        var problems = Check(
            Rows(("A.param", [(1, "v")])),
            Rows(("A.param", [(1, "v")])),
            mods: ("m", Rows(("A.param", [(1, "MOD")]))));

        problems.Should().ContainSingle().Which.Should().Contain("A.param").And.Contain("row 1");
    }

    [Fact]
    public void IgnoresRowsAModDidNotAuthor()
    {
        var problems = Check(
            Rows(("A.param", [])),
            Rows(("A.param", [(1, "v")])),
            mods: ("m", Rows(("A.param", [(1, "v")]))));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DoesNotFlagARowTwoModsBothAuthored()
    {
        // A row both mods authored is settled by priority and noted by the merge itself; demanding both survive would
        // fail every legitimate merge.
        var problems = Check(
            Rows(("A.param", [(1, "ONE")])),
            Rows(("A.param", [(1, "v")])),
            mods: [("one", Rows(("A.param", [(1, "ONE")]))), ("two", Rows(("A.param", [(1, "TWO")])))]);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void RequiresTheMergedOutputToClaimTheInstalledBuild()
    {
        var problems = Check(Rows(), Rows(), "11601000");

        problems.Should().ContainSingle().Which.Should().Contain("11601000").And.Contain(Installed);
    }

    [Fact]
    public void KeepsRowsTheNewBaselineAdded()
    {
        // A patch added rows to a table. They come from the baseline and must still be there after a rebase.
        var problems = Check(
            Rows(("A.param", [(1, "MOD")])),
            Rows(("A.param", [(1, "v"), (99, "new-in-1.17")])),
            mods: ("m", Rows(("A.param", [(1, "MOD")]))));

        problems.Should().ContainSingle().Which.Should().Contain("99");
    }

    [Fact]
    public void ARowTheModShipsUnchangedIsNotACommitmentOfTheBaseline()
    {
        // The mod ships the whole table, so it mentions every row; one it did not change is its silence, not a row the
        // baseline contributed.
        var problems = Check(
            Rows(("A.param", [])),
            Rows(("A.param", [(1, "v"), (2, "v")])),
            mods: ("m", Rows(("A.param", [(1, "v"), (2, "v")]))));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void ATableTheMergedFileLacksReportsItsAuthoredRows()
    {
        var problems = Check(
            Rows(),
            Rows(("A.param", [(1, "v")])),
            mods: ("m", Rows(("A.param", [(1, "MOD")]))));

        problems.Should().ContainSingle().Which.Should().Contain("A.param").And.Contain("row 1");
    }

    [Fact]
    public void RowsByTableSkipsATableItCannotRead()
    {
        // The merge never splices rows of an unreadable entry, so there are no transplanted rows in it to verify.
        var rows = RebaseVerifier.RowsByTable([new BinderEntry(1, "GR\\Cutscene.param", "junk"u8.ToArray())]);

        rows.Should().BeEmpty();
    }

    [Fact]
    public void RowsByTableKeysRowsByIdUnderTheTableName()
    {
        var blob = ParamFixture.Make([5, 9], stride: 4, fill: id => [(byte)id, (byte)id, 0, 0]);

        var rows = RebaseVerifier.RowsByTable([new BinderEntry(1, "GR\\gameparam\\A.param", blob)]);

        rows["A.param"].Keys.Should().BeEquivalentTo([5, 9]);
        rows["A.param"][9].Should().Equal(9, 9, 0, 0);
    }

    private static IReadOnlyList<string> Golden(string merged)
        => RebaseVerifier.Verify(
            GoldenFilesTests.Read(merged),
            GoldenFilesTests.Read("rebase_baseline.bin"),
            [("alpha", GoldenFilesTests.Read("rebase_mod.bin"))],
            BuildIds.Make(),
            T);

    [Fact]
    public void ARebaseThatKeptEverythingPassesOverGoldenRegulations()
        => Golden("rebase_merged_ok.bin").Should().BeEmpty();

    [Fact]
    public void GivesTheSameProblemStringsAsErmOverGoldenRegulations()
        => Golden("rebase_merged_lossy.bin").Should().Equal(
            System.Text.Encoding.UTF8.GetString(GoldenFilesTests.Read("rebase_lossy_problems.txt")).Split('\n'));

    [Fact]
    public void TheMergedRegulationIsDecryptedOnlyOnce()
    {
        // The build stamp and the rows come out of the same pass: a freshly merged file is never one that was read
        // before, so asking twice costs seconds nobody gets back.
        var seen = new List<string>();

        RebaseVerifier.Verify(
            GoldenFilesTests.Read("rebase_merged_ok.bin"),
            GoldenFilesTests.Read("rebase_baseline.bin"),
            [],
            BuildIds.Make(),
            T,
            (blob, traits) =>
            {
                seen.Add(Convert.ToBase64String(blob));
                return Regulation.Unpack(blob, traits);
            });

        seen.Should().OnlyHaveUniqueItems().And.HaveCount(2);
    }

    [Fact]
    public void AnIdentityWithoutARegulationVersionCannotBeVerifiedAgainst()
    {
        var act = () => RebaseVerifier.Verify(
            GoldenFilesTests.Read("rebase_merged_ok.bin"), GoldenFilesTests.Read("rebase_baseline.bin"), [], new BuildIdentity("2.7.0.0", "1"), T);

        act.Should().Throw<ArgumentException>().WithMessage("*regulation version*");
    }
}
