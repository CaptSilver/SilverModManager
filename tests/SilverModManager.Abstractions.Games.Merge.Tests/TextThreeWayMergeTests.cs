using FluentAssertions;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class TextThreeWayMergeTests
{
    private const string V = "a\nb\nc\nd\ne\n";
    private static (string Text, List<IMergeNote> Notes) Merge(string? vanilla, string preferred, string other)
    {
        var notes = new List<IMergeNote>();
        return (TextThreeWayMerge.Merge(vanilla, preferred, other, "High", "Low", "scripts/x.ws", notes), notes);
    }

    [Fact] public void NonOverlappingHunksFromBothSidesApply()
    {
        var (text, notes) = Merge(V, "A\nb\nc\nd\ne\n", "a\nb\nc\nd\nE\n");
        text.Should().Be("A\nb\nc\nd\nE\n"); notes.Should().BeEmpty();
    }
    [Fact] public void OverlappingHunksTakeThePreferredSideAndNoteTheLoss()
    {
        var (text, notes) = Merge(V, "a\nB1\nc\nd\ne\n", "a\nB2\nc\nd\ne\n");
        text.Should().Be("a\nB1\nc\nd\ne\n");
        var kept = notes.Should().ContainSingle().Which.Should().BeOfType<LinesKept>().Which;
        kept.Should().Be(new LinesKept("scripts/x.ws", 2, 2, "High", "Low"));
        kept.Loss.Should().Be(new MergeLoss("Low", "High"));
        kept.Summary().Should().Be("both High and Low change `scripts/x.ws` lines 2–2; High's are used.");
    }
    [Fact] public void WithoutVanillaTheMergeIsTwoWayAndThePreferredSideWinsEveryDifference()
    {
        var (text, notes) = Merge(null, "a\nB1\nc\n", "a\nB2\nc\nd\n");
        text.Should().Be("a\nB1\nc\n");
        notes.OfType<LinesKept>().Should().HaveCount(2);
    }
    [Fact] public void ThreeModsFoldInPriorityOrderAgainstTheSameVanilla()
    {
        var (step1, _) = Merge(V, "A\nb\nc\nd\ne\n", "a\nB\nc\nd\ne\n");
        var (text, notes) = Merge(V, step1, "a\nb\nc\nd\nE\n");
        text.Should().Be("A\nB\nc\nd\nE\n"); notes.Should().BeEmpty();
    }
    [Fact] public void ALossBetweenTwoLowerModsNamesTheModWhoseLinesAreInTheOutput()
    {
        // A does not touch line 2; B and C both do. The loss is C's to B, not to A and not to a stand-in.
        var notes = new List<IMergeNote>();
        var (text, owners, _) = TextThreeWayMerge.Fold("a\nb\nc\n", [("A\nb\nc\n", "A"), ("a\nB\nc\n", "B"), ("a\nC\nc\n", "C")], "scripts/x.ws", notes);
        text.Should().Be("A\nB\nc\n");
        owners.Should().Equal("A", "B", "A");   // a vanilla line is owned by the first contributor
        notes.Should().ContainSingle().Which.Should().Be(new LinesKept("scripts/x.ws", 2, 2, "B", "C"));
    }
    [Fact] public void AnInsertionAtTheEdgeOfAChangeIsNotAConflict()
    {
        // Other inserts between vanilla lines 1 and 2 (i == 1); preferred changes vanilla lines 2..2 ([1, 2)): the insertion goes before the change.
        var before = Merge(V, "a\nB\nc\nd\ne\n", "a\nX\nb\nc\nd\ne\n");
        before.Text.Should().Be("a\nX\nB\nc\nd\ne\n"); before.Notes.Should().BeEmpty();
        // Insertion at i == 2 == end of the change: it goes after.
        var after = Merge(V, "a\nB\nc\nd\ne\n", "a\nb\nX\nc\nd\ne\n");
        after.Text.Should().Be("a\nB\nX\nc\nd\ne\n"); after.Notes.Should().BeEmpty();
    }
    [Fact] public void AnInsertionStrictlyInsideAChangeIsAConflict()
    {
        // Preferred rewrites vanilla lines 2..3 ([1, 3)); other inserts between them (i == 2).
        var (text, notes) = Merge(V, "a\nB\nC\nd\ne\n", "a\nb\nX\nc\nd\ne\n");
        text.Should().Be("a\nB\nC\nd\ne\n");
        notes.Should().ContainSingle().Which.Should().Be(new LinesKept("scripts/x.ws", 2, 3, "High", "Low"));
    }
    [Fact] public void TwoDifferentInsertionsAtOnePointConflictAndIdenticalOnesApplyOnce()
    {
        var (text, notes) = Merge(V, "a\nX\nb\nc\nd\ne\n", "a\nY\nb\nc\nd\ne\n");
        text.Should().Be("a\nX\nb\nc\nd\ne\n");
        notes.Should().ContainSingle().Which.Should().Be(new LinesKept("scripts/x.ws", 2, 2, "High", "Low"));
        var same = Merge(V, "a\nX\nb\nc\nd\ne\n", "a\nX\nb\nc\nd\ne\n");
        same.Text.Should().Be("a\nX\nb\nc\nd\ne\n"); same.Notes.Should().BeEmpty();
    }
    [Fact] public void HunksAreCountedAcrossTheFold()
        => TextThreeWayMerge.Fold("a\nb\nc\n", [("A\nb\nc\n", "A"), ("a\nB\nc\n", "B"), ("a\nb\nC\n", "C")], "x", []).Hunks.Should().Be(3);
    [Fact] public void ThePreferredSidesLineEndingIsKept()
    {
        var (text, _) = Merge("a\r\nb\r\n", "a\r\nB\r\n", "a\nb\nc\n");
        text.Should().Be("a\r\nB\r\nc\r\n");
    }
    [Fact] public void AModShippingVanillaUnchangedLosesNothing()          // Review Focus 3
    {
        var (text, notes) = Merge(V, V, "a\nB\nc\nd\ne\n");
        text.Should().Be("a\nB\nc\nd\ne\n"); notes.Should().BeEmpty();
    }
    [Fact] public void TrailingNewlineStateFollowsThePreferredSide()        // Review Focus 4
    {
        Merge("a\nb", "a\nB", "a\nb\n").Text.Should().Be("a\nB");
        Merge("", "", "x\n").Text.Should().Be("x\n");   // an insertion against an empty vanilla applies; the empty preferred side has no ending to impose
        Merge("", "", "").Text.Should().Be("");
    }
    [Fact] public void InvalidUtf8IsAnInputProblemNotACrash()
    {
        var act = () => TextThreeWayMerge.Instance.Fold([new([0xFF, 0xFE, 0x00], "High"), new("a"u8.ToArray(), "Low")], [], hasVanilla: false, "x", []);
        act.Should().Throw<MergeInputException>().WithMessage("*High*UTF-8*");
    }
    [Fact] public void AByteOrderMarkOnTheFirstContributorIsKept()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var merged = TextThreeWayMerge.Instance.Fold([new([.. bom, .. "a\n"u8], "High"), new("a\n"u8.ToArray(), "Low")], "a\n"u8, hasVanilla: true, "x", []);
        merged.Bytes.Take(3).Should().Equal(bom);
        merged.Hunks.Should().Be(0);
    }

    [Fact] public void IdenticalRewritesAtDifferentBasePositionsApplyOnce()
    {
        string[] v = ["", "}", "", "", "x4", "", "", ""];
        string[] a = ["", "}", "", "", "", "REW", "", "", ""];
        string[] b = ["", "}", "", "", "", "REW", "", "", "", "NEWU"];
        var notes = new List<IMergeNote>();
        var (text, _, _) = TextThreeWayMerge.Fold(Join(v), [(Join(a), "High"), (Join(b), "Low")], "x", notes);
        string D(string[] x) => string.Join(" ", LineDiff.Compare(v, x).Select(o => (o.Kind == LineDiffKind.Added ? "+" : o.Kind == LineDiffKind.Removed ? "-" : "=") + o.Text));
        notes.Should().BeEmpty(string.Join(" | ", notes.Select(n => n.Describe())) + " A: " + D(a) + " B: " + D(b));
        var lines = LineDiff.SplitLines(text);
        lines.Count(l => l == "REW").Should().Be(1);
        lines.Count(l => l == "NEWU").Should().Be(1);
        lines.Should().Equal(a.Append("NEWU"));
    }

    [Fact] public void AnInsertionAtTheEndOfTheFileAppendsAfterTheLastLine()
    {
        var (text, notes) = Merge(V, "A\nb\nc\nd\ne\n", "a\nb\nc\nd\ne\nf\n");
        text.Should().Be("A\nb\nc\nd\ne\nf\n"); notes.Should().BeEmpty();
    }

    [Fact] public void TwoWayLossesAreNamedForTheFirstContributor()
    {
        var (_, notes) = Merge(null, "a\nB1\nc\n", "a\nB2\nc\nd\n");
        notes.OfType<LinesKept>().Should().Equal(
            new LinesKept("scripts/x.ws", 2, 2, "High", "Low"),
            new LinesKept("scripts/x.ws", 3, 3, "High", "Low"));
    }

    [Fact] public void ANonIdenticalOverlapNamesEachConflictingKeptMod()
    {
        var notes = new List<IMergeNote>();
        TextThreeWayMerge.Fold("a\nb\nc\nd\n", [("A\nb\nc\nd\n", "A"), ("a\nb\nC\nd\n", "B"), ("A2\nB2\nC2\nd\n", "C")], "p", notes);
        notes.Should().Equal(new LinesKept("p", 1, 1, "A", "C"), new LinesKept("p", 3, 3, "B", "C"));
    }

    [Fact] public void NoVanillaNotesDescribeThemselves()
    {
        new NoVanillaBase("p").Describe().Should().Be("p: no vanilla copy; merged two-way, the higher-priority mod wins every difference");
        new VanillaBaseUntrusted("p", "it was patched").Describe().Should().Be("p: the game's own copy was not used as the base (it was patched); merged two-way");
        new LinesKept("p", 2, 3, "H", "L").Describe().Should().Be("lines 2–3: both mods changed them; kept H's");
    }

    [Fact] public void ABomOnVanillaAndLowerModsIsStrippedBeforeDiffing()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var merged = TextThreeWayMerge.Instance.Fold(
            [new("a\nB\n"u8.ToArray(), "High"), new([.. bom, .. "A\nb\n"u8], "Low")], [.. bom, .. "a\nb\n"u8], hasVanilla: true, "x", []);
        merged.Bytes.Should().Equal("A\nB\n"u8.ToArray());
    }

    [Fact] public void TheVanillaBeingInvalidUtf8NamesTheGamesCopy()
    {
        var act = () => TextThreeWayMerge.Instance.Fold([new("a"u8.ToArray(), "High")], [0xFF, 0xFE], hasVanilla: true, "x", []);
        act.Should().Throw<MergeInputException>().WithMessage("the game's copy*UTF-8*");
    }

    private static string Join(IEnumerable<string> lines) => string.Concat(lines.Select(l => l + "\n"));

    [Fact] public void EditsToDisjointRegionsAllApplyAndLoseNothing()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var n = rng.Next(0, 12);
            var mods = rng.Next(2, 4);
            var vanilla = Enumerable.Range(0, n).Select(i => $"v{i}").ToArray();
            // Each base line and each insertion slot belongs to at most one contributor.
            var lineOwner = Enumerable.Range(0, n).Select(_ => rng.Next(-1, mods)).ToArray();
            var lineEdit = Enumerable.Range(0, n).Select(_ => rng.Next(0, 2)).ToArray();   // 0 replace, 1 delete
            var slotOwner = Enumerable.Range(0, n + 1).Select(_ => rng.Next(-1, mods)).ToArray();
            // An insertion strictly inside another mod's changed block is a real conflict, not what this test is about.
            for (var i = 1; i < n; i++)
                if (lineOwner[i - 1] >= 0 && lineOwner[i - 1] == lineOwner[i] && slotOwner[i] != lineOwner[i]) slotOwner[i] = -1;

            var expected = new List<string>();
            var texts = Enumerable.Range(0, mods).Select(_ => new List<string>()).ToArray();
            for (var i = 0; i <= n; i++)
            {
                if (slotOwner[i] >= 0)
                {
                    expected.Add($"ins{i}");
                    for (var m = 0; m < mods; m++) if (m == slotOwner[i]) texts[m].Add($"ins{i}");
                }
                if (i == n) break;
                var o = lineOwner[i];
                if (o >= 0 && lineEdit[i] == 0) expected.Add($"rep{i}");
                else if (o < 0) expected.Add(vanilla[i]);
                for (var m = 0; m < mods; m++)
                {
                    if (m == o) { if (lineEdit[i] == 0) texts[m].Add($"rep{i}"); }
                    else texts[m].Add(vanilla[i]);
                }
            }

            var notes = new List<IMergeNote>();
            var order = Enumerable.Range(0, mods).OrderBy(_ => rng.Next()).ToArray();
            var (text, owners, _) = TextThreeWayMerge.Fold(Join(vanilla), order.Select(m => (Join(texts[m]), $"M{m}")).ToArray(), "p", notes);
            notes.Should().BeEmpty($"seed {seed}: {string.Join(" | ", notes.Select(n => n.Describe()))}; vanilla [{string.Join(",", vanilla)}] mods {string.Join(" / ", order.Select(m => string.Join(",", texts[m])))}");
            LineDiff.SplitLines(text).Should().Equal(expected, $"seed {seed}");
            owners.Should().HaveCount(expected.Count);
        }
    }

    [Fact] public void TheSameEditFromSeveralModsAppearsOnce()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var n = rng.Next(1, 12);
            var vanilla = Enumerable.Range(0, n).Select(i => $"v{i}").ToList();
            List<string> edited = [], third = [], expected = [];
            for (var i = 0; i <= n; i++)
            {
                var kind = rng.Next(0, 4);   // 0 untouched, 1 replace, 2 delete, 3 insert before
                if (kind == 3) edited.Add($"ins{i}");
                if (kind == 3) expected.Add($"ins{i}");
                if (i == n) break;
                if (kind == 1) { edited.Add($"rep{i}"); expected.Add($"rep{i}"); }
                else if (kind != 2) edited.Add(vanilla[i]);
                // a third mod edits some lines the first two leave alone
                var mine = kind == 0 && rng.Next(0, 2) == 0;
                third.Add(mine ? $"mine{i}" : vanilla[i]);
                if (kind == 3) third.Insert(third.Count - 1, "");
                if (kind == 0) expected.Add(mine ? $"mine{i}" : vanilla[i]);
                else if (kind == 3) expected.Add(vanilla[i]);
            }
            third.RemoveAll(l => l.Length == 0);
            var notes = new List<IMergeNote>();
            var (text, _, _) = TextThreeWayMerge.Fold(Join(vanilla), [(Join(edited), "A"), (Join(edited), "B")], "p", notes);
            notes.Should().BeEmpty($"seed {seed}");
            LineDiff.SplitLines(text).Should().Equal(edited, $"seed {seed}");

            // with the third mod ranked anywhere, the shared edit still appears once and the third mod's lines all land
            var all = new (string, string)[] { (Join(edited), "A"), (Join(edited), "B"), (Join(third), "C") }.OrderBy(_ => rng.Next()).ToArray();
            notes.Clear();
            var merged = LineDiff.SplitLines(TextThreeWayMerge.Fold(Join(vanilla), all, "p", notes).Text);
            notes.Should().BeEmpty($"seed {seed}");
            merged.Should().Equal(expected, $"seed {seed}");
        }
    }
}
