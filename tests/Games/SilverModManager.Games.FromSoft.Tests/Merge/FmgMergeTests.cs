using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>Every rule is run for a BND4 game and a BND3 game: the merge reads and rebuilds through the game's binder.</summary>
public class FmgMergeTests
{
    public static TheoryData<string> Games => ["Elden Ring", "Test Souls"];

    private static FromSoftFormatTraits TraitsOf(string game) => game switch
    {
        "Elden Ring" => EldenRingGame.Definition.Formats,
        _ => TestSoulsGame.Definition.Formats,
    };

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, string?>> Tables(params (int Entry, (int Id, string? Text)[] Rows)[] tables) =>
        tables.ToDictionary(
            t => t.Entry,
            t => (IReadOnlyDictionary<int, string?>)t.Rows.ToDictionary(r => r.Id, r => r.Text));

    private static byte[] Msgbnd(FromSoftFormatTraits traits, params (int Entry, (int Id, string? Text)[] Rows)[] tables) =>
        MsgbndFixture.Build(Tables(tables), traits);

    private static Dictionary<int, string?> Table(byte[] archive, FromSoftFormatTraits traits, int entry) =>
        MsgbndFixture.ReadBack(archive, traits)[entry];

    // --- union ---

    [Theory, MemberData(nameof(Games))]
    public void DisjointIdsFromBothSidesSurvive(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(100, "Clever's weapon")]));
        var other = Msgbnd(t, (1, [(200, "Resurrect"), (201, "Teleport")]));
        MsgbndFixture.ReadBack(FmgMerge.Union(@base, other, t), t).Should().BeEquivalentTo(
            new Dictionary<int, Dictionary<int, string?>> { [1] = new() { [100] = "Clever's weapon", [200] = "Resurrect", [201] = "Teleport" } });
    }

    [Theory, MemberData(nameof(Games))]
    public void BaseWinsAGenuineCollision(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(401106, "!!! MOD NOTE !!!")]));
        var other = Msgbnd(t, (1, [(401106, "Failed to save.")]));
        Table(FmgMerge.Union(@base, other, t), t, 1)[401106].Should().Be("!!! MOD NOTE !!!");
    }

    [Theory, MemberData(nameof(Games))]
    public void EntriesOnlyInBaseAreUntouched(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(1, "a")]), (2, [(5, "only in base")]));
        var other = Msgbnd(t, (1, [(2, "b")]));
        var merged = MsgbndFixture.ReadBack(FmgMerge.Union(@base, other, t), t);
        merged[2].Should().Equal(new Dictionary<int, string?> { [5] = "only in base" });
        merged[1].Should().Equal(new Dictionary<int, string?> { [1] = "a", [2] = "b" });
    }

    [Theory, MemberData(nameof(Games))]
    public void OutputUsesTheGamesPackedWriteFormat(string game)
    {
        var t = TraitsOf(game);
        var merged = FmgMerge.Union(Msgbnd(t, (1, [(1, "a")])), Msgbnd(t, (1, [(2, "b")])), t);
        merged.AsSpan(0x28, 4).ToArray().Should().Equal("DFLT"u8.ToArray());
        merged.Should().Equal(Dcx.Write(Dcx.Read(merged, t), t.PackedWrite));
    }

    [Theory, MemberData(nameof(Games))]
    public void AnEntryOnlyInOtherIsRefused(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(1, "a")]));
        var other = Msgbnd(t, (1, [(2, "b")]), (99, [(3, "new entry")]));
        var act = () => FmgMerge.Union(@base, other, t);
        act.Should().Throw<FmgMergeException>().WithMessage("*99*");
    }

    [Theory, MemberData(nameof(Games))]
    public void BaseNullDefersToOthersRealText(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(5, null), (6, "kept")]));
        var other = Msgbnd(t, (1, [(5, "real text from other")]));
        Table(FmgMerge.Union(@base, other, t), t, 1).Should().Equal(
            new Dictionary<int, string?> { [5] = "real text from other", [6] = "kept" });
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionLeavesAnUntouchedTableByteIdentical(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(1, "a")]), (2, [(20, "same"), (21, "same")]));
        var other = Msgbnd(t, (1, [(2, "b")]));
        var binder = Binders.For(t);
        var before = binder.Read(Dcx.Read(@base, t)).ToDictionary(e => e.Id, e => e.Data);
        var after = binder.Read(Dcx.Read(FmgMerge.Union(@base, other, t), t)).ToDictionary(e => e.Id, e => e.Data);
        after[2].Should().Equal(before[2]);
        after[1].Should().NotEqual(before[1]);
    }

    [Fact]
    public void UnionMatchesTheArchiveErmMerges()
    {
        var t = EldenRingGame.Definition.Formats;
        var @base = Msgbnd(t, (1, [(100, "mod note"), (101, null)]), (2, [(20, "x")]));
        var other = Msgbnd(t, (1, [(100, "vanilla"), (101, "real"), (200, "new")]), (2, [(20, "x"), (21, "y")]));
        Dcx.Read(FmgMerge.Union(@base, other, t), t).Should().Equal(GoldenFilesTests.Read("msgbnd_union.bnd"));
    }

    // --- union notes ---

    [Theory, MemberData(nameof(Games))]
    public void UnionNamesTheKeptModForASharedIdWithDifferingText(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(401106, "!!! MOD NOTE !!!"), (5, "same")]));
        var other = Msgbnd(t, (1, [(401106, "Failed to save."), (5, "same")]));
        var notes = new List<IMergeNote>();

        var merged = FmgMerge.Union(@base, other, t, notes, "Mod A", "Mod B");

        notes.Should().Equal(new TextKept(1, 401106, "Mod A", "Mod B"));
        merged.Should().Equal(FmgMerge.Union(@base, other, t));
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesNothingForIdenticalSharedText(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(7, "same")]));
        var other = Msgbnd(t, (1, [(7, "same")]));
        var notes = new List<IMergeNote>();
        FmgMerge.Union(@base, other, t, notes);
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesNothingWhenBaseHasNoTextForTheId(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(7, null)]));
        var other = Msgbnd(t, (1, [(7, "filled in")]));
        var notes = new List<IMergeNote>();
        Table(FmgMerge.Union(@base, other, t, notes), t, 1)[7].Should().Be("filled in");
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesUseGenericNamesWhenNoneAreGiven(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(7, "a")]));
        var other = Msgbnd(t, (1, [(7, "b")]));
        var notes = new List<IMergeNote>();
        FmgMerge.Union(@base, other, t, notes);
        notes.Should().Equal(new TextKept(1, 7, "the preferred mod", "the other mod"));
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesNothingWhenTheOtherSideHasNoTextForTheId(string game)
    {
        var t = TraitsOf(game);
        var @base = Msgbnd(t, (1, [(7, "base text")]));
        var other = Msgbnd(t, (1, [(7, null)]));
        var notes = new List<IMergeNote>();
        FmgMerge.Union(@base, other, t, notes);
        notes.Should().BeEmpty();
    }

    [Fact]
    public void NoTwoNoteKindsRenderTheSameSentence()
    {
        IMergeNote[] notes =
        [
            new RowCollision(1, 2), new RivalRow(1, 2), new UnreadableParam(1), new RowConflict(1, 2, "A", "B"),
            new TextKept(1, 2, "A", "B"), new TextureKept("tex", "A", "B"),
            new TextureDeletionConflict("tex", "A", "B", KeptDeleted: true),
            new TextureDeletionConflict("tex", "A", "B", KeptDeleted: false),
        ];
        notes.Select(n => n.Describe()).Should().OnlyHaveUniqueItems();
        new TextKept(1, 2, "A", "B").Describe().Should().Be("entry 1 text 2: both mods ship it and differ; kept A's");
    }

    // --- three-way ---

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayTakesTheOtherSidesEditWhenBaseMatchesVanilla(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla text")]));
        var @base = Msgbnd(t, (1, [(10, "vanilla text")]));
        var other = Msgbnd(t, (1, [(10, "modded text")]));
        Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1)[10].Should().Be("modded text");
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayKeepsTheBaseEditWhenOtherMatchesVanilla(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla text")]));
        var @base = Msgbnd(t, (1, [(10, "base's authored edit")]));
        var other = Msgbnd(t, (1, [(10, "vanilla text")]));
        Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1)[10].Should().Be("base's authored edit");
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayKeepsTheLeadersTextWhenBothSidesEditedTheSameRow(string game)
    {
        // erm's fmg_three_way raises when both sides changed one id differently. Here the accumulated side's text
        // wins and the loss is recorded, because the loadout's priority decides a real conflict.
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla")]));
        var @base = Msgbnd(t, (1, [(10, "base version")]));
        var other = Msgbnd(t, (1, [(10, "other version")]));
        var notes = new List<IMergeNote>();

        var merged = FmgMerge.ThreeWay(@base, other, vanilla, t, notes, "Clever's Moveset", "Item Randomizer");

        Table(merged, t, 1)[10].Should().Be("base version");
        notes.Should().Equal(new TextKept(1, 10, "Clever's Moveset", "Item Randomizer"));
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayConflictWithoutANotesListStillKeepsTheLeader(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla")]));
        var @base = Msgbnd(t, (1, [(10, "base version")]));
        var other = Msgbnd(t, (1, [(10, "other version")]));
        Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1)[10].Should().Be("base version");
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayWithoutAConflictAddsNoNotes(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla"), (11, "v")]));
        var @base = Msgbnd(t, (1, [(10, "same"), (11, "v")]));
        var other = Msgbnd(t, (1, [(10, "same"), (11, "other edit")]));
        var notes = new List<IMergeNote>();
        FmgMerge.ThreeWay(@base, other, vanilla, t, notes);
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayAllowsBothSidesMakingTheIdenticalEdit(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla")]));
        var @base = Msgbnd(t, (1, [(10, "same new text")]));
        var other = Msgbnd(t, (1, [(10, "same new text")]));
        Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1)[10].Should().Be("same new text");
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayDoesNotResurrectARowTheBaseDeleted(string game)
    {
        // A present id with no text is an authored deletion; if it read as absent, the other side's vanilla text
        // would refill the row.
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla text")]));
        var @base = Msgbnd(t, (1, [(10, null)]));
        var other = Msgbnd(t, (1, [(10, "vanilla text")]));
        var row = Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1);
        row.Should().ContainKey(10);
        row[10].Should().BeNull();
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayAppliesADeletionTheOtherSideAuthored(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "vanilla text")]));
        var @base = Msgbnd(t, (1, [(10, "vanilla text")]));
        var other = Msgbnd(t, (1, [(10, null)]));
        var row = Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1);
        row.Should().ContainKey(10);
        row[10].Should().BeNull();
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayAddsARowNeitherBaseNorVanillaHas(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "a")]));
        var @base = Msgbnd(t, (1, [(10, "a")]));
        var other = Msgbnd(t, (1, [(10, "a"), (11, "brand new")]));
        Table(FmgMerge.ThreeWay(@base, other, vanilla, t), t, 1)[11].Should().Be("brand new");
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayRefusesAMismatchedEntrySet(string game)
    {
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "a")]));
        var @base = Msgbnd(t, (1, [(10, "a")]));
        var other = Msgbnd(t, (1, [(10, "a")]), (99, [(1, "extra")]));
        var act = () => FmgMerge.ThreeWay(@base, other, vanilla, t);
        act.Should().Throw<FmgMergeException>().WithMessage("*entr*99*");
    }

    [Theory, MemberData(nameof(Games))]
    public void ThreeWayLeavesUntouchedEntriesByteIdentical(string game)
    {
        // Fmg.Write shares strings vanilla stores twice, so re-serialising an entry nobody edited would churn bytes.
        var t = TraitsOf(game);
        var vanilla = Msgbnd(t, (1, [(10, "a")]), (2, [(20, "untouched"), (21, "untouched")]));
        var @base = Msgbnd(t, (1, [(10, "a")]), (2, [(20, "untouched"), (21, "untouched")]));
        var other = Msgbnd(t, (1, [(10, "changed")]), (2, [(20, "untouched"), (21, "untouched")]));
        var binder = Binders.For(t);
        var before = binder.Read(Dcx.Read(@base, t)).ToDictionary(e => e.Id, e => e.Data);
        var after = binder.Read(Dcx.Read(FmgMerge.ThreeWay(@base, other, vanilla, t), t)).ToDictionary(e => e.Id, e => e.Data);
        after[2].Should().Equal(before[2]);
        after[1].Should().NotEqual(before[1]);
    }

    [Fact]
    public void ThreeWayMatchesTheArchiveErmMerges()
    {
        var t = EldenRingGame.Definition.Formats;
        var vanilla = Msgbnd(t, (1, [(100, "vanilla"), (101, "real")]), (2, [(20, "x")]));
        var @base = Msgbnd(t, (1, [(100, "mod note"), (101, null)]), (2, [(20, "x")]));
        var other = Msgbnd(t, (1, [(100, "vanilla"), (101, "real"), (200, "new")]), (2, [(20, "x")]));
        Dcx.Read(FmgMerge.ThreeWay(@base, other, vanilla, t), t).Should().Equal(GoldenFilesTests.Read("msgbnd_three_way.bnd"));
    }
}
