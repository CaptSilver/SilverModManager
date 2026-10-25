using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using FormatRegulation = SilverModManager.Games.FromSoft.Formats.Regulation;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// Mods built for different game builds folded onto the installed game's regulation, each against the vanilla of the
/// build it was made for. Builds here: V1 is the older one, V2 the game's. The patch between them edited row 1, added
/// row 5 and removed row 3.
/// </summary>
public class ContributorAncestorTests
{
    private const string Path = "regulation.bin";
    private const string V1 = "11601000";
    private const string V2 = "11701000";
    private const string SpType = "SP_EFFECT_PARAM_ST";
    private const int Entry = 1;

    private static FromSoftFormatTraits ER => EldenRingGame.Definition.Formats;

    private static byte[] Row(params byte[] head) => [.. head, .. new byte[8 - head.Length]];

    private static byte[] Reg(string version, params (int Id, byte[] Data)[] rows)
        => RegulationFixture.Build(new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [Entry] = (SpType, rows.ToDictionary(r => r.Id, r => r.Data), 8),
        }, version, ER);

    private static readonly byte[] Vanilla1 = Reg(V1, (1, Row()), (2, Row()), (3, Row()), (4, Row()));

    private static readonly byte[] Game = Reg(V2, (1, Row(0x11)), (2, Row()), (4, Row()), (5, Row(0x55)));

    /// <summary>A second contributor that ships its build's vanilla as it is; a path with one contributor is not merged at all.</summary>
    private static readonly (string Name, byte[] Regulation, byte[] Ancestor) Untouched = ("untouched", Vanilla1, Vanilla1);

    private static Dictionary<int, byte[]> RowsOf(byte[] regulation)
        => Param.Read(FormatRegulation.Entries(regulation, ER).Single().Data).Rows.ToDictionary(r => r.Id, r => r.Data);

    private static ProviderFile File(string package, ulong priority) => new(package, Path, Hash.From((ulong)package.GetHashCode()), priority, package);

    /// <summary>Folds the mods (highest priority first) onto <see cref="Game"/>; each is compared against the vanilla of the build it carries.</summary>
    private static MergePlan Plan(params (string Name, byte[] Regulation, byte[] Ancestor)[] mods)
    {
        var files = mods.Select((m, i) => File(m.Name, (ulong)(mods.Length - i))).ToArray();
        var contents = mods.ToDictionary(m => m.Name, m => m.Regulation);
        var ancestors = mods.ToDictionary(m => m.Name, m => m.Ancestor);
        var live = new BuildIdentity("1.0", "1", V2);
        var rebase = RegulationRebase.For(f => FormatRegulation.Version(FormatRegulation.Unpack(contents[f.PackageId], ER)), () => live, ER);
        return MergePlanner.Plan(
            MergePathIndex.Build(files, ER, new HashSet<string>()), f => contents[f.PackageId], (_, f) => ancestors[f.PackageId], _ => Game, ER,
            MergeStrategyBindings.For, rebase);
    }

    [Fact]
    public void ModsOfTwoBuildsFoldToTheGamesRowsPlusEachModsOwnEdits()
    {
        var older = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row()), (4, Row()));
        var current = Reg(V2, (1, Row(0x11)), (2, Row()), (4, Row(0xbb)), (5, Row(0x55)));

        var plan = Plan(("older", older, Vanilla1), ("current", current, Game));

        plan.Refusal.Should().BeNull();
        var rows = RowsOf(plan.Merged.Single().Bytes);
        rows.Keys.Should().BeEquivalentTo([1, 2, 4, 5], "the patch removed row 3 and no mod edited it");
        rows[1].Should().Equal(Row(0x11), "the older mod ships the pre-patch row unchanged, which is not an edit");
        rows[2].Should().Equal(Row(0xaa));
        rows[4].Should().Equal(Row(0xbb));
        rows[5].Should().Equal(Row(0x55));
        FormatRegulation.Version(FormatRegulation.Unpack(plan.Merged.Single().Bytes, ER)).Should().Be(V2);
    }

    [Fact]
    public void AnOlderModWithAnUntouchedOneIsRebasedAndVerifiedAgainstItsOwnAncestor()
    {
        var older = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row()), (4, Row()));

        var plan = Plan(("older", older, Vanilla1), Untouched);

        plan.Refusal.Should().BeNull("the row the patch edited is shipped unchanged by the mod, so the mod never authored it");
        RowsOf(plan.Merged.Single().Bytes)[2].Should().Equal(Row(0xaa));
    }

    [Fact]
    public void ARowTheGameAddedAndTwoOlderModsAddedUnderTheSameIdIsACollisionAtEveryStep()
    {
        var first = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row()), (4, Row()));
        var second = Reg(V1, (1, Row()), (2, Row()), (3, Row()), (4, Row()), (5, Row(0x77)));

        var plan = Plan(("first", first, Vanilla1), ("second", second, Vanilla1));

        plan.Refusal.Should().BeNull();
        RowsOf(plan.Merged.Single().Bytes)[5].Should().Equal(Row(0x77), "the mod's content wins over a row the game added, as it does at the first step");
        plan.Merged.Single().Notes.Should().ContainSingle(n => n is RowCollision).Which.Should().Be(new RowCollision(Entry, 5));
        plan.Merged.Single().Notes.Should().NotContain(n => n is RivalRow);
    }

    [Fact]
    public void ARowTwoModsAddedAfterAnEarlierModTookTheIdIsStillARival()
    {
        var first = Reg(V1, (1, Row()), (2, Row()), (3, Row()), (4, Row()), (5, Row(0x66)));
        var second = Reg(V1, (1, Row()), (2, Row()), (3, Row()), (4, Row()), (5, Row(0x77)));

        var plan = Plan(("first", first, Vanilla1), ("second", second, Vanilla1));

        plan.Refusal.Should().BeNull();
        RowsOf(plan.Merged.Single().Bytes)[5].Should().Equal(Row(0x66));
        plan.Merged.Single().Notes.OfType<RowCollision>().Should().ContainSingle();
        plan.Merged.Single().Notes.OfType<RivalRow>().Should().ContainSingle();
    }

    [Fact]
    public void ARowConflictAfterAnEarlierModNamesTheGameAsTheOwnerOfTheBytesThatStayed()
    {
        var first = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row()), (4, Row()));
        var second = Reg(V1, (1, Row(0x22)), (2, Row()), (3, Row()), (4, Row()));

        var plan = Plan(("first", first, Vanilla1), ("second", second, Vanilla1));

        plan.Refusal.Should().BeNull("a clash with the patch is a warning and the game's bytes stay");
        RowsOf(plan.Merged.Single().Bytes)[1].Should().Equal(Row(0x11));
        plan.Merged.Single().Notes.OfType<RowConflict>().Should().ContainSingle()
            .Which.Should().Be(new RowConflict(Entry, 1, MergeNames.Game, "second", KeptIsGame: true));
    }

    [Fact]
    public void ARowTheGameAndAnOlderModEditedOnDifferentBytesMergesAndIsNotedAsCrossBuild()
    {
        var older = Reg(V1, (1, Row(0, 0, 0, 0, 0, 0, 0, 0x99)), (2, Row()), (3, Row()), (4, Row()));

        var plan = Plan(("older", older, Vanilla1), Untouched);

        plan.Refusal.Should().BeNull();
        RowsOf(plan.Merged.Single().Bytes)[1].Should().Equal(Row(0x11, 0, 0, 0, 0, 0, 0, 0x99));
        plan.Merged.Single().Notes.Should().ContainSingle().Which.Should().Be(new CrossBuildRow(Entry, 1, V1, V2));
    }

    [Fact]
    public void ARowOnlyTheOlderModEditedIsNotCrossBuild()
    {
        var older = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row()), (4, Row()));

        var plan = Plan(("older", older, Vanilla1), Untouched);

        plan.Refusal.Should().BeNull();
        plan.Merged.Single().Notes.Should().BeEmpty();
    }

    [Fact]
    public void ModsEditingRowsThePatchRemovedAreRefusedTogetherListingEveryRow()
    {
        var first = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row(0xcc)), (4, Row()));
        var second = Reg(V1, (1, Row()), (2, Row()), (3, Row(0xdd)), (4, Row(0xee)));

        var plan = Plan(("first", first, Vanilla1), ("second", second, Vanilla1));

        plan.Merged.Should().BeEmpty();
        var reason = plan.Refusal.Should().NotBeNull().And.Subject.As<MergeRefusal>().Reason;
        reason.Should().Contain("removed").And.Contain("first").And.Contain("second").And.Contain("row 3");
        reason.Should().NotContain("row 4", "the patch left row 4 alone");
        reason.Split('\n').Count(line => line.Contains("row 3")).Should().Be(2, "one line per mod and row");
    }

    [Fact]
    public void AModWithNoAncestorIsNamedInTheRefusal()
    {
        var older = Reg(V1, (1, Row()), (2, Row(0xaa)), (3, Row()), (4, Row()));
        var files = new[] { File("older", 2), File("untouched", 1) };
        var plan = MergePlanner.Plan(
            MergePathIndex.Build(files, ER, new HashSet<string>()), f => f.PackageId == "older" ? older : Vanilla1, (_, f) => f.PackageId == "older" ? null : Vanilla1, _ => Game, ER);

        plan.Refusal!.Reason.Should().StartWith("older ").And.Contain("none is available");
    }
}
