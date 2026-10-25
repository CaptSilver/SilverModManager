using FluentAssertions;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Build;
using SilverModManager.Games.FromSoft.Tests.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

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
        // The merged copy differs from what the mod ships: the mod inherited the row, so its bytes are not owed.
        var problems = Check(
            Rows(("A.param", [(1, "w")])),
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
    public void ARowTheModShipsUnchangedStillCannotBeDroppedFromTheMergedFile()
    {
        // The mod's silence about a row it inherited does not license dropping it: only a mod that changed or removed
        // the row can.
        var problems = Check(
            Rows(("A.param", [])),
            Rows(("A.param", [(1, "v"), (2, "v")])),
            mods: ("m", Rows(("A.param", [(1, "v"), (2, "v")]))));

        problems.Should().BeEquivalentTo(["baseline A.param row 1 was dropped by the rebase", "baseline A.param row 2 was dropped by the rebase"]);
    }

    [Fact]
    public void ARowAModRemovedMayBeDroppedFromTheMergedFile()
    {
        var problems = Check(
            Rows(("A.param", [(2, "v")])),
            Rows(("A.param", [(1, "v"), (2, "v")])),
            mods: ("m", Rows(("A.param", [(2, "v")]))));

        problems.Should().BeEmpty();
    }

    private static IReadOnlyList<string> CheckWithAncestors(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> merged,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> baseline,
        IReadOnlySet<(string Table, int Id, string ModId)>? clashes = null,
        params (string Mod, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Ancestor)[] mods)
        => RebaseVerifier.CheckAgainstAncestors(Installed, merged, baseline, mods, Installed, clashes: clashes);

    [Fact]
    public void KeepsRowsTheNewBaselineAdded()
    {
        // A patch added rows to a table. They come from the baseline and must still be there after a rebase.
        var problems = CheckWithAncestors(
            Rows(("A.param", [(1, "MOD")])),
            Rows(("A.param", [(1, "v"), (99, "new-in-1.17")])),
            mods: ("m", Rows(("A.param", [(1, "MOD")])), Rows(("A.param", [(1, "v")]))));

        problems.Should().ContainSingle().Which.Should().Contain("99");
    }

    [Fact]
    public void ARowThePatchChangedAndTheOlderModShipsUnchangedIsNotAuthoredByIt()
    {
        var problems = CheckWithAncestors(
            Rows(("A.param", [(1, "new")])),
            Rows(("A.param", [(1, "new")])),
            mods: ("old", Rows(("A.param", [(1, "old")])), Rows(("A.param", [(1, "old")]))));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void RowsWiderInTheBaselineThanInTheModAreComparedOnTheirCommonPrefix()
    {
        // Ten one-row tables are eight bytes wider in the game and in the mod's own vanilla than in the mod; the extra
        // bytes are not an edit, so the mod neither owes the row nor licenses dropping it.
        var baseline = Rows(("A.param", [(1, "ROW!padding")]));
        var mod = ("m", Rows(("A.param", [(1, "ROW!")])), Rows(("A.param", [(1, "ROW!padding")])));

        CheckWithAncestors(Rows(("A.param", [(1, "ROW!padding")])), baseline, mods: mod).Should().BeEmpty();
        CheckWithAncestors(Rows(("A.param", [])), baseline, mods: mod)
            .Should().ContainSingle().Which.Should().Contain("row 1").And.Contain("dropped");
    }

    [Fact]
    public void AnEditOnBytesThePatchLeftAloneSurvivesAnOverlapWithAPatchEditOnOtherBytes()
    {
        // ancestor "ab", patch made it "Xb", mod made it "aB"; the merge carries both.
        var problems = CheckWithAncestors(
            Rows(("A.param", [(1, "XB")])),
            Rows(("A.param", [(1, "Xb")])),
            mods: ("m", Rows(("A.param", [(1, "aB")])), Rows(("A.param", [(1, "ab")]))));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void AnEditThatLostItsByteInAClashIsReportedNamingTheRow()
    {
        // ancestor "ab", patch "Xb", mod "Zb": same byte, different value; the merge kept the game's row whole.
        var merged = Rows(("A.param", [(1, "Xb")]));
        var baseline = Rows(("A.param", [(1, "Xb")]));

        var problems = CheckWithAncestors(merged, baseline,
            mods: ("m", Rows(("A.param", [(1, "Zb")])), Rows(("A.param", [(1, "ab")]))));

        problems.Should().ContainSingle().Which.Should().Contain("m").And.Contain("A.param").And.Contain("row 1");
    }

    [Fact]
    public void AClashTheMergeAlreadyReportedIsNotAFailure()
    {
        var merged = Rows(("A.param", [(1, "Xb")]));
        var baseline = Rows(("A.param", [(1, "Xb")]));

        var problems = CheckWithAncestors(merged, baseline,
            clashes: new HashSet<(string, int, string)> { ("A.param", 1, "m") },
            mods: ("m", Rows(("A.param", [(1, "Zb")])), Rows(("A.param", [(1, "ab")]))));

        problems.Should().BeEmpty("a mod-against-patch clash keeps the game's bytes and is reported as a warning");
    }

    [Fact]
    public void ACurrentModDoesNotMakeADroppedPatchRowPass()
    {
        // The older mod's ancestor lacks row 5, so its silence is no claim; the current mod inherited row 5 and left it alone.
        var problems = CheckWithAncestors(
            Rows(("A.param", [(1, "v")])),
            Rows(("A.param", [(1, "v"), (5, "patch")])),
            mods:
            [
                ("old", Rows(("A.param", [(1, "v")])), Rows(("A.param", [(1, "v")]))),
                ("current", Rows(("A.param", [(1, "v"), (5, "patch")])), Rows(("A.param", [(1, "v"), (5, "patch")]))),
            ]);

        problems.Should().ContainSingle().Which.Should().Contain("row 5").And.Contain("dropped");
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

    // The golden mod is the older build's vanilla with row 1 edited; the patch added row 99 after it.
    private static readonly byte[] GoldenAncestor = RegulationFixture.Build(
        new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = ("SP_EFFECT_PARAM_ST", new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)'v', 8).ToArray(), [2] = Enumerable.Repeat((byte)'v', 8).ToArray() }, 8),
        }, "11601000", T);

    private static IReadOnlyList<string> Golden(string merged)
        => RebaseVerifier.Verify(
            GoldenFilesTests.Read(merged),
            GoldenFilesTests.Read("rebase_baseline.bin"),
            [("alpha", GoldenFilesTests.Read("rebase_mod.bin"), GoldenAncestor)],
            BuildIds.Make(),
            T,
            []);

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
