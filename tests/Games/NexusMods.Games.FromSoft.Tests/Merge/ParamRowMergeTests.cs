using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>
/// The golden files are what erm's own param-row merge produces for the same three regulations. Where erm refuses, the
/// merge here settles the case by priority instead, and those tests have no golden.
/// </summary>
public class ParamRowMergeTests
{
    private const string SpType = "SP_EFFECT_PARAM_ST";
    private const string Version = "07D7R6\0\0";

    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private sealed record Table(int Entry, string Type, int Stride, (int Id, byte[] Data)[] Rows);

    private static Table Sp(int entry, int stride, params (int Id, byte[] Data)[] rows) => new(entry, SpType, stride, rows);

    private static Table Of(int entry, string type, int stride, params (int Id, byte[] Data)[] rows) => new(entry, type, stride, rows);

    /// <summary>A regulation of param tables. Rows are written in id order; an id given twice keeps its given order.</summary>
    private static byte[] Reg(params Table[] tables)
    {
        var entries = tables.OrderBy(t => t.Entry).Select(t =>
        {
            var rows = t.Rows.OrderBy(r => r.Id).ToArray();
            var next = 0;
            var blob = ParamFixture.Make(rows.Select(r => r.Id).ToArray(), t.Stride, t.Type, fill: _ => rows[next++].Data);
            return (t.Entry, $"{t.Type}.param", blob);
        }).ToList();
        return RegulationFixture.BuildFromEntries(entries, Version, T);
    }

    private static byte[] Rep(int value, int count) => Enumerable.Repeat((byte)value, count).ToArray();

    private static byte[] Zeros(int count) => new byte[count];

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static Dictionary<int, byte[]> RowsOf(byte[] regulation, int entry)
        => Param.Read(EntryOf(regulation, entry)).Rows.ToDictionary(r => r.Id, r => r.Data);

    private static byte[] EntryOf(byte[] regulation, int entry) => Regulation.Entries(regulation, T).Single(e => e.Id == entry).Data;

    private static byte[] Run(byte[] @base, byte[] other, byte[] vanilla, List<IMergeNote>? notes = null, bool baseIsGame = false,
        string? baseName = null, string? otherName = null)
        => ParamRowMerge.Merge(@base, other, vanilla, T, notes, baseIsGame, baseName, otherName);

    private static void MatchesErm(string name, byte[] actual)
        => actual.Should().Equal(GoldenFilesTests.Read($"param_rows_{name}.bin"));

    [Fact]
    public void TakesTheOtherSidesEditWhenBaseMatchesVanilla()
    {
        var van = Reg(Sp(1, 8, (100, Zeros(8))));
        var @base = Reg(Sp(1, 8, (100, Zeros(8))));
        var other = Reg(Sp(1, 8, (100, Rep(0xff, 8))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[100].Should().Equal(Rep(0xff, 8));
        MatchesErm("other_edit_when_base_matches_vanilla", merged);
    }

    [Fact]
    public void KeepsTheBaseEditWhenOtherMatchesVanilla()
    {
        // Row 174 in the shipped data: one mod changed effectEndurance, the other still carries vanilla's value.
        // Copying the whole param across would revert the first mod's edit.
        var van = Reg(Sp(1, 8, (174, Zeros(8))));
        var @base = Reg(Sp(1, 8, (174, Rep(0xbb, 8))));
        var other = Reg(Sp(1, 8, (174, Zeros(8))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[174].Should().Equal(Rep(0xbb, 8));
        MatchesErm("base_edit_kept_when_other_matches_vanilla", merged);
    }

    [Fact]
    public void InsertsARowNeitherBaseNorVanillaHas()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x01, 8)), (360401, Rep(0xaa, 8))));
        var merged = Run(@base, other, van);
        var rows = RowsOf(merged, 1);
        rows[360401].Should().Equal(Rep(0xaa, 8));
        rows[1].Should().Equal(Rep(0x01, 8));
        MatchesErm("inserts_a_row_neither_base_nor_vanilla_has", merged);
    }

    [Fact]
    public void KeepsTheLeadersRowWhenBothSidesChangedTheSameRowDifferently()
    {
        // erm refuses this: its byte merge raises when both sides moved a byte to different values. Mod priority is
        // meant to settle a real conflict, so the accumulated side keeps its row and the loss is named.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Rep(0x11, 8))));
        var other = Reg(Sp(1, 8, (5, Rep(0x22, 8))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes, baseName: "Mod A", otherName: "Mod B");
        RowsOf(merged, 1)[5].Should().Equal(Rep(0x11, 8));
        notes.Should().Equal(new RowConflict(1, 5, "Mod A", "Mod B"));
    }

    [Fact]
    public void KeepsTheLeadersRowWhenBothSidesChangedTheSameByte()
    {
        // erm refuses this: both sides moved byte 0 to different values. The higher-priority side keeps its row.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Cat([0xaa], Zeros(7)))));
        var other = Reg(Sp(1, 8, (5, Cat([0xbb], Zeros(7)))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes, baseName: "Mod A", otherName: "Mod B");
        RowsOf(merged, 1)[5].Should().Equal(Cat([0xaa], Zeros(7)));
        notes.Should().Equal(new RowConflict(1, 5, "Mod A", "Mod B"));
    }

    [Fact]
    public void DropsTheLosersOtherEditsToAConflictingRow()
    {
        // The other mod also changed byte 0, which the base left alone, before the clash at byte 4. That edit goes with
        // the rest of its row: a field is several bytes, so half of the loser's row is not a row either mod wrote.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Cat(Zeros(4), [0xaa], Zeros(3)))));
        var other = Reg(Sp(1, 8, (5, Cat([0xcc], Zeros(3), [0xbb], Zeros(3)))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes, baseName: "Mod A", otherName: "Mod B");
        RowsOf(merged, 1)[5].Should().Equal(Cat(Zeros(4), [0xaa], Zeros(3)));
        notes.Should().Equal(new RowConflict(1, 5, "Mod A", "Mod B"));
    }

    [Fact]
    public void ADefaultNameIsGivenWhenTheCallerHasNone()
    {
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Rep(0x11, 8))));
        var other = Reg(Sp(1, 8, (5, Rep(0x22, 8))));
        var notes = new List<IMergeNote>();
        Run(@base, other, van, notes, baseIsGame: true);
        notes.Should().Equal(new RowConflict(1, 5, "the game", "the other mod"));
    }

    [Fact]
    public void ALaterContributorLosesAClashWithAnEarlierOne()
    {
        // erm refuses this clash between the second and third mods. Folding is what makes it visible: the second mod's
        // edit becomes part of the base the third is compared against, and that earlier (higher priority) edit stays.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Zeros(8))));
        var second = Reg(Sp(1, 8, (5, Cat([0xbb], Zeros(7)))));
        var third = Reg(Sp(1, 8, (5, Cat([0xcc], Zeros(7)))));
        var notes = new List<IMergeNote>();
        var afterSecond = Run(@base, second, van, notes, baseName: "Mod A", otherName: "Mod B");
        var afterThird = Run(afterSecond, third, van, notes, baseName: "Mod B", otherName: "Mod C");
        RowsOf(afterThird, 1)[5].Should().Equal(Cat([0xbb], Zeros(7)));
        notes.Should().Equal(new RowConflict(1, 5, "Mod B", "Mod C"));
    }

    [Fact]
    public void SplicesTwoEditsToDifferentBytesOfOneFieldIntoAValueNeitherModWrote()
    {
        // Known gap, pinned so it stays visible. The merge sees bytes, not fields: one mod sets a 4-byte field to
        // 0x00000001 and the other sets it to 0x00020000, no byte is shared, and the result is 0x00020001, which
        // neither author wrote. A paramdef-aware merge would treat this as a conflict and change this outcome.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Cat([0x01, 0x00, 0x00, 0x00], Zeros(4)))));
        var other = Reg(Sp(1, 8, (5, Cat([0x00, 0x00, 0x02, 0x00], Zeros(4)))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes);
        RowsOf(merged, 1)[5].Should().Equal(Cat([0x01, 0x00, 0x02, 0x00], Zeros(4)));
        notes.Should().BeEmpty();
    }

    [Fact]
    public void LeavesAnUntouchedParamByteIdentical()
    {
        // Byte-comparing params to decide what changed over-reports roughly threefold, because tools disagree on how the
        // strings offset is rounded; a param the other side did not change has to be copied through, not re-serialised.
        var van = Reg(Sp(1, 8, (1, Rep(0x01, 8))), Of(2, "OTHER_ST", 8, (9, Rep(0x09, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x01, 8))), Of(2, "OTHER_ST", 8, (9, Rep(0xcc, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x22, 8))), Of(2, "OTHER_ST", 8, (9, Rep(0x09, 8))));
        var merged = Run(@base, other, van);
        EntryOf(merged, 2).Should().Equal(EntryOf(@base, 2));
        EntryOf(merged, 1).Should().NotEqual(EntryOf(@base, 1));
        MatchesErm("untouched_param_stays_byte_identical", merged);
    }

    [Fact]
    public void ToleratesAStrideThatOnlyDiffersByTrailingPadding()
    {
        // Several shipped params derive a different stride per file because the game's writer pads where re-saving tools do
        // not. The bytes over the shorter row agree, so there is no change to merge.
        var van = Reg(Sp(1, 12, (1, Cat(Rep(0xab, 8), Zeros(4)))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var merged = Run(@base, other, van);
        EntryOf(merged, 1).Should().Equal(EntryOf(@base, 1));
        MatchesErm("stride_differing_by_trailing_padding", merged);
    }

    [Fact]
    public void TransplantsWhenOnlyVanillaHasThePadding()
    {
        // Base and other agree on the stride, so the row transplants; vanilla's width only affects the comparison.
        var van = Reg(Sp(1, 12, (1, Cat(Rep(0xab, 8), Zeros(4)))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x99, 8))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[1].Should().Equal(Rep(0x99, 8));
        MatchesErm("padding_only_in_vanilla", merged);
    }

    [Fact]
    public void RefusesARowItCannotFitIntoTheBase()
    {
        // Two rows, because a width measured from one row is padding, not a layout fact.
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Rep(0xcd, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Rep(0xcd, 8))));
        var other = Reg(Sp(1, 12, (1, Rep(0x99, 12)), (2, Rep(0xcd, 12))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*stride*");
    }

    [Fact]
    public void TransplantsIntoASingleRowParamTheGamePaddedWider()
    {
        // The base is the game's own file, where a one-row param's derived stride carries padding a mod's re-saved copy
        // does not. The mod's edit still belongs in that row, and the base keeps its own padding.
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 12, (1, Cat(Rep(0xab, 8), Zeros(4)))));
        var other = Reg(Sp(1, 8, (1, Rep(0x99, 8))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[1].Should().Equal(Cat(Rep(0x99, 8), Zeros(4)));
        MatchesErm("single_row_param_the_game_padded_wider", merged);
    }

    [Fact]
    public void RefusesASingleRowWhoseExtraBytesAreNotPadding()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var other = Reg(Sp(1, 12, (1, Rep(0x99, 12))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*padding*");
    }

    [Fact]
    public void KeepsTheModsRowWhenTheGameAndAModEachInventOne()
    {
        // Rebasing onto a newer build meets rows vanilla never had. There is no ancestor to locate either edit against
        // and renumbering is not an option (a shop row is reached by id range), so the mod's row wins and the drop is named.
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (101896, Rep(0x11, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (101896, Rep(0x22, 8))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes, baseIsGame: true);
        RowsOf(merged, 1)[101896].Should().Equal(Rep(0x22, 8));
        notes.Should().Equal(new RowCollision(1, 101896));
        MatchesErm("game_and_mod_each_invent_a_row", merged);
    }

    [Fact]
    public void KeepsTheLeadersRowWhenTwoModsEachInventOne()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (80020000, Rep(0x11, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (80020000, Rep(0x22, 8))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes);
        RowsOf(merged, 1)[80020000].Should().Equal(Rep(0x11, 8));
        notes.Should().Equal(new RivalRow(1, 80020000));
    }

    [Fact]
    public void KeepsARowOnlyTheGameAdded()
    {
        // A game patch's own rows are in no mod's file; the collision rule must fire only when a mod claimed the id too.
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (99, Rep(0x33, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var merged = Run(@base, other, van, baseIsGame: true);
        RowsOf(merged, 1)[99].Should().Equal(Rep(0x33, 8));
        MatchesErm("row_only_the_game_added", merged);
    }

    [Fact]
    public void StillInsertsARowOnlyTheModAdded()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (99, Rep(0x44, 8))));
        var merged = Run(@base, other, van, baseIsGame: true);
        RowsOf(merged, 1)[99].Should().Equal(Rep(0x44, 8));
        MatchesErm("row_only_the_mod_added", merged);
    }

    [Fact]
    public void StillMergesBytewiseWhenVanillaHasTheRow()
    {
        // The collision rule applies only when vanilla lacks the row; with an ancestor, disjoint edits are no collision.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Cat([0xaa], Zeros(7)))));
        var other = Reg(Sp(1, 8, (5, Cat(Zeros(4), [0xbb], Zeros(3)))));
        var notes = new List<IMergeNote>();
        var merged = Run(@base, other, van, notes);
        RowsOf(merged, 1)[5].Should().Equal(Cat([0xaa], Zeros(3), [0xbb], Zeros(3)));
        notes.Should().BeEmpty();
        MatchesErm("disjoint_fields_of_one_row", merged);
    }

    [Fact]
    public void RefusesWhenTheBaseDroppedARowTheOtherSideEdited()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Zeros(8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Rep(0x77, 8))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*row 2*");
    }

    [Fact]
    public void RefusesWhenTheOtherSideDroppedARowTheBaseEdited()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Zeros(8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Rep(0x77, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*row 2*");
    }

    [Fact]
    public void KeepsASharedDropWhenTheOtherSideEdited()
    {
        // Neither side has the row any more, so they agree; calling that a conflict would stop an apply over agreement.
        var van = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x55, 8))));
        var @base = Reg(Sp(1, 8, (1, Zeros(8))));
        var other = Reg(Sp(1, 8, (1, Rep(0xbb, 8))));
        var merged = Run(@base, other, van);
        var rows = RowsOf(merged, 1);
        rows.Should().NotContainKey(5);
        rows[1].Should().Equal(Rep(0xbb, 8));
        MatchesErm("shared_drop_other_edited", merged);
    }

    [Fact]
    public void KeepsASharedDropWhenTheBaseEdited()
    {
        var van = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x55, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xaa, 8))));
        var other = Reg(Sp(1, 8, (1, Zeros(8))));
        var merged = Run(@base, other, van);
        var rows = RowsOf(merged, 1);
        rows.Should().NotContainKey(5);
        rows[1].Should().Equal(Rep(0xaa, 8));
        MatchesErm("shared_drop_base_edited", merged);
    }

    [Fact]
    public void NamesTheEntriesTheThreeRegulationsDisagreeOn()
    {
        // The likeliest cause is a game patch adding a param; counts alone leave the user nothing to act on.
        var van = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x01, 8))), Of(99, "NEW_PARAM_ST", 8, (1, Rep(0x02, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x02, 8))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*entry 99*");
    }

    [Fact]
    public void DropsARowOnlyTheOtherSideDeleted()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0x11, 8)), (2, Rep(0x22, 8)), (3, Rep(0x33, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x11, 8)), (2, Rep(0x22, 8)), (3, Rep(0x33, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x11, 8)), (3, Rep(0x33, 8))));
        var merged = Run(@base, other, van);
        var rows = RowsOf(merged, 1);
        rows.Should().NotContainKey(2);
        rows[1].Should().Equal(Rep(0x11, 8));
        rows[3].Should().Equal(Rep(0x33, 8));
        MatchesErm("row_only_the_other_side_deleted", merged);
    }

    [Fact]
    public void DeletesAndInsertsInTheSamePass()
    {
        // A shrink and a grow move the row table, the row data and the strings block by a net offset neither does alone.
        var van = Reg(Sp(1, 8, (1, Rep(0x11, 8)), (2, Rep(0x22, 8)), (3, Rep(0x33, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x11, 8)), (2, Rep(0x22, 8)), (3, Rep(0x33, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x11, 8)), (3, Rep(0x33, 8)), (99, Rep(0x99, 8))));
        var merged = Run(@base, other, van);
        var entry = EntryOf(merged, 1);
        var reparsed = Param.Read(entry);
        reparsed.Rows.Select(r => r.Id).Should().Equal(1, 3, 99);
        reparsed.ParamType.Should().Be(SpType);
        Param.Write(reparsed).Should().Equal(entry);
        MatchesErm("delete_and_insert_in_one_pass", merged);
    }

    [Fact]
    public void TheOutputIsALoadableRegulation()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var other = Reg(Sp(1, 8, (1, Rep(0x02, 8))));
        var merged = Run(@base, other, van);
        Regulation.Unpack(merged, T).AsSpan(0, 4).ToArray().Should().Equal("BND4"u8.ToArray());
        Regulation.Entries(merged, T).Should().HaveCount(1);
    }

    [Fact]
    public void SkipsAnEntryBothModsAlreadyAgreeOn()
    {
        var van = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0x02, 8))));
        var merged = Run(@base, @base, van);
        EntryOf(merged, 1).Should().Equal(EntryOf(@base, 1));
        MatchesErm("entry_both_mods_already_agree_on", merged);
    }

    [Fact]
    public void LeavesEntriesNeitherSideTouchedUnparsed()
    {
        // The exits are per entry: some of vanilla's own entries only parse after a tool re-saves them, so an entry
        // nobody changed must not be read. These are not PARAMs, and the merge would throw if it tried.
        var junkA = "not a param, base only"u8.ToArray();
        var junkB = "not a param, shared"u8.ToArray();
        var junkC = "not a param, vanilla only"u8.ToArray();
        var changed = ParamFixture.Make([1], 8, SpType, fill: _ => Rep(0x01, 8));
        var changedOther = ParamFixture.Make([1], 8, SpType, fill: _ => Rep(0x02, 8));

        byte[] Build(params (int Id, string Name, byte[] Data)[] entries) => RegulationFixture.BuildFromEntries(entries, Version, T);
        var van = Build((1, "1.param", changed), (2, "2.param", junkC), (3, "3.param", junkC));
        var @base = Build((1, "1.param", changed), (2, "2.param", junkA), (3, "3.param", junkB));
        var other = Build((1, "1.param", changedOther), (2, "2.param", junkC), (3, "3.param", junkB));

        var merged = Run(@base, other, van);
        EntryOf(merged, 2).Should().Equal(junkA);
        EntryOf(merged, 3).Should().Equal(junkB);
        RowsOf(merged, 1)[1].Should().Equal(Rep(0x02, 8));
    }

    [Fact]
    public void MergesTwoModsEditingDifferentFieldsOfARow()
    {
        // A field is a contiguous byte range, so edits to disjoint bytes are edits to disjoint fields. Two shipped mods
        // share many weapon rows and never a byte.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Cat([0xaa], Zeros(7)))));
        var other = Reg(Sp(1, 8, (5, Cat(Zeros(4), [0xbb], Zeros(3)))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[5].Should().Equal(Cat([0xaa], Zeros(3), [0xbb], Zeros(3)));
        MatchesErm("disjoint_fields_of_one_row", merged);
    }

    [Fact]
    public void AcceptsBothSidesMakingTheIdenticalEdit()
    {
        // One regulation is built on top of the other, so both carry the same added row. The row has to be one vanilla
        // lacks to pin anything: with an ancestor the byte merge reaches the same answer by itself.
        var van = Reg(Sp(1, 8, (5, Zeros(8))));
        var @base = Reg(Sp(1, 8, (5, Zeros(8)), (900, Cat([0xaa], Zeros(7)))));
        var other = Reg(Sp(1, 8, (5, Cat([0xbb], Zeros(7))), (900, Cat([0xaa], Zeros(7)))));
        var merged = Run(@base, other, van);
        var rows = RowsOf(merged, 1);
        rows[900].Should().Equal(Cat([0xaa], Zeros(7)));
        rows[5].Should().Equal(Cat([0xbb], Zeros(7)));
        MatchesErm("identical_edit_on_both_sides", merged);
    }

    [Fact]
    public void TrimsATransplantedRowToTheBasesNarrowerLayout()
    {
        // Where the excess past the base's width really is padding, the row fits and the transplant goes through.
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xab, 8))));
        var other = Reg(Sp(1, 12, (1, Cat(Rep(0x99, 8), Zeros(4)))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[1].Should().Equal(Rep(0x99, 8));
        MatchesErm("transplanted_row_trimmed_to_narrower_base", merged);
    }

    [Fact]
    public void RefusesARowBothSidesChangedAtDifferentWidths()
    {
        // Three widths give the byte merge nothing to line up; the wider side's tail would be dropped without a word.
        var van = Reg(Sp(1, 8, (1, Rep(0xab, 8)), (2, Rep(0xcd, 8))));
        var @base = Reg(Sp(1, 12, (1, Rep(0x11, 12)), (2, Cat(Rep(0xcd, 8), Zeros(4)))));
        var other = Reg(Sp(1, 8, (1, Rep(0x22, 8)), (2, Rep(0xcd, 8))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*different widths*");
    }

    [Fact]
    public void KeepsAnUntouchedFieldFromVanilla()
    {
        var van = Reg(Sp(1, 8, (5, Cat([0x00, 0x00, 0x77, 0x00], Zeros(4)))));
        var @base = Reg(Sp(1, 8, (5, Cat([0xaa, 0x00, 0x77, 0x00], Zeros(4)))));
        var other = Reg(Sp(1, 8, (5, Cat([0x00, 0x00, 0x77, 0x00], Zeros(3), [0xbb]))));
        var merged = Run(@base, other, van);
        RowsOf(merged, 1)[5].Should().Equal(Cat([0xaa, 0x00, 0x77, 0x00], Zeros(3), [0xbb]));
        MatchesErm("untouched_field_stays_vanillas", merged);
    }

    [Fact]
    public void FoldsAcrossThreeModsWithVanillaConstant()
    {
        // N contributors are N-1 merges with vanilla as the reference every time; each mod's rows must survive the folds after it.
        var van = Reg(Sp(1, 8, (1, Zeros(8)), (2, Zeros(8)), (3, Zeros(8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xaa, 8)), (2, Zeros(8)), (3, Zeros(8))));
        var second = Reg(Sp(1, 8, (1, Zeros(8)), (2, Rep(0xbb, 8)), (3, Zeros(8))));
        var third = Reg(Sp(1, 8, (1, Zeros(8)), (2, Zeros(8)), (3, Rep(0xcc, 8))));
        var merged = Run(Run(@base, second, van), third, van);
        var rows = RowsOf(merged, 1);
        rows[1].Should().Equal(Rep(0xaa, 8));
        rows[2].Should().Equal(Rep(0xbb, 8));
        rows[3].Should().Equal(Rep(0xcc, 8));
        MatchesErm("fold_across_three_mods", merged);
    }

    [Fact]
    public void FoldsThreeModsEditingDifferentFieldsOfOneRow()
    {
        var van = Reg(Sp(1, 8, (7, Zeros(8))));
        var @base = Reg(Sp(1, 8, (7, Cat([0x01], Zeros(7)))));
        var second = Reg(Sp(1, 8, (7, Cat(Zeros(3), [0x02], Zeros(4)))));
        var third = Reg(Sp(1, 8, (7, Cat(Zeros(6), [0x03], Zeros(1)))));
        var merged = Run(Run(@base, second, van), third, van);
        RowsOf(merged, 1)[7].Should().Equal(0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x03, 0x00);
        MatchesErm("fold_three_mods_editing_different_fields", merged);
    }

    [Fact]
    public void TakesTheModsTableWholeWhenAnEntryWillNotParse()
    {
        // The base still matches vanilla, so the mod's table is the only edit there and swapping it in loses nothing.
        // It is a substitution the report has to name.
        var unreadable = "not a PARAM at all"u8.ToArray();
        var van = RegulationFixture.BuildFromEntries([(1, "1.param", unreadable)], Version, T);
        var other = Reg(Sp(1, 8, (1, Rep(0x02, 8))));
        var notes = new List<IMergeNote>();
        var merged = Run(van, other, van, notes);
        EntryOf(merged, 1).Should().Equal(EntryOf(other, 1));
        notes.Should().Equal(new UnreadableParam(1));
        MatchesErm("unreadable_entry_taken_whole", merged);
    }

    [Fact]
    public void RefusesAnUnparseableEntryBothSidesChanged()
    {
        // A real edit on each side and no way to read either: picking one silently is the loss this merge exists to avoid.
        var van = Reg(Sp(1, 8, (1, Rep(0x01, 8))));
        var @base = RegulationFixture.BuildFromEntries([(1, "1.param", "not a PARAM at all"u8.ToArray())], Version, T);
        var other = Reg(Sp(1, 8, (1, Rep(0x03, 8))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*entry 1*");
    }

    [Fact]
    public void RefusesADuplicatedParamBothSidesChanged()
    {
        // An id is the only handle this merge has on a row, and here it does not name one. Keying by id would collapse
        // the two copies and the shadowed edit would compare equal to vanilla: dropped with the apply reporting success.
        var van = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x50, 8)), (5, Rep(0x51, 8))));
        var @base = Reg(Sp(1, 8, (1, Rep(0xaa, 8)), (5, Rep(0x50, 8)), (5, Rep(0x51, 8))));
        var other = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x99, 8)), (5, Rep(0x51, 8))));
        var act = () => Run(@base, other, van);
        act.Should().Throw<MergeException>().WithMessage("*more than once*");
    }

    [Fact]
    public void KeepsTheBaseWhenADuplicatedParamHasNoOtherEdit()
    {
        // The common shape: every mod ships this table row for row as vanilla but re-saved, so the entries differ and
        // the whole-entry exit cannot fire. Nothing to merge, and the base's own edit survives.
        var van = Reg(Sp(1, 12, (1, Zeros(12)), (5, Cat(Rep(0x50, 8), Zeros(4))), (5, Cat(Rep(0x51, 8), Zeros(4)))));
        var @base = Reg(Sp(1, 12, (1, Cat(Rep(0xaa, 8), Zeros(4))), (5, Cat(Rep(0x50, 8), Zeros(4))), (5, Cat(Rep(0x51, 8), Zeros(4)))));
        var other = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x50, 8)), (5, Rep(0x51, 8))));
        var merged = Run(@base, other, van);
        EntryOf(merged, 1).Should().Equal(EntryOf(@base, 1));
        MatchesErm("duplicated_param_without_an_other_edit", merged);
    }

    [Fact]
    public void TakesTheWholeDuplicatedParamWhenOnlyTheOtherEdited()
    {
        var van = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x50, 8)), (5, Rep(0x51, 8))));
        var other = Reg(Sp(1, 8, (1, Zeros(8)), (5, Rep(0x99, 8)), (5, Rep(0x51, 8))));
        var merged = Run(van, other, van);
        EntryOf(merged, 1).Should().Equal(EntryOf(other, 1));
        MatchesErm("duplicated_param_only_the_other_edited", merged);
    }

    [Fact]
    public void DecryptsEachRegulationOnlyOnce()
    {
        // The AES pass over a shipped regulation costs seconds. Writing the result back must reuse the base's payload
        // instead of decrypting it a second time.
        var van = Reg(Sp(1, 8, (100, Zeros(8))));
        var @base = Reg(Sp(1, 8, (100, Zeros(8)), (5, Rep(0x11, 8))));
        var other = Reg(Sp(1, 8, (100, Rep(0xff, 8))));
        var seen = new List<string>();
        byte[] Spy(ReadOnlySpan<byte> blob, FromSoftFormatTraits traits)
        {
            seen.Add(Convert.ToHexString(blob));
            return Regulation.Unpack(blob, traits);
        }

        var merged = ParamRowMerge.Merge(@base, other, van, T, null, false, null, null, Spy);
        seen.Should().HaveCount(3);
        seen.Distinct().Should().HaveCount(3);
        var rows = RowsOf(merged, 1);
        rows[100].Should().Equal(Rep(0xff, 8));
        rows[5].Should().Equal(Rep(0x11, 8));
    }

    [Fact]
    public void AnUnchangedRegulationStillComesBackRepackedWithTheGamesWindow()
    {
        // The game's own regulation declares a 64 MiB window that me3 cannot load; going back through Pack is what
        // normalises it, so a merge with nothing to do must not hand the input back.
        var wide = T with { RegulationWrite = T.RegulationWrite! with { WindowLog = 26 } };
        var entries = new List<(int Id, string Name, byte[] Data)> { (1, "1.param", ParamFixture.Make([1], 8, SpType, fill: _ => Rep(0x01, 8))) };
        var regulation = RegulationFixture.BuildFromEntries(entries, Version, wide);
        WindowLogOf(regulation).Should().Be(26, "the input is the wide-window shape this test is about");

        var merged = Run(regulation, regulation, regulation);
        WindowLogOf(merged).Should().Be(T.RegulationWrite!.WindowLog);
        Regulation.Unpack(merged, T).Should().Equal(Regulation.Unpack(regulation, T));
    }

    private static int WindowLogOf(byte[] regulation)
    {
        var dcx = AesCbc.DecryptCbc([..T.RegulationKey], regulation[..Regulation.IvSize], regulation[Regulation.IvSize..]);
        return 10 + (dcx[Dcx.HeaderSize + 5] >> 3);
    }

    [Fact]
    public void ARowCollisionIsDescribed()
    {
        var text = new RowCollision(145, 101896).Describe();
        text.Should().Contain("145").And.Contain("101896").And.Contain("mod").And.Contain("dropped");
    }

    [Fact]
    public void ARivalRowIsDescribed()
    {
        var text = new RivalRow(123, 80020000).Describe();
        text.Should().Contain("123").And.Contain("80020000").And.Contain("two mods").And.Contain("dropped");
    }

    [Fact]
    public void AnUnreadableParamIsDescribed()
    {
        var text = new UnreadableParam(30).Describe();
        text.Should().Contain("30").And.Contain("mod");
    }

    [Fact]
    public void ARowConflictNamesTheRowAndBothMods()
    {
        new RowConflict(4, 70, "Mod A", "Mod B").Describe().Should().Be(
            "entry 4 row 70: both mods changed it differently; kept Mod A's row and dropped Mod B's edits to it");
    }
}
