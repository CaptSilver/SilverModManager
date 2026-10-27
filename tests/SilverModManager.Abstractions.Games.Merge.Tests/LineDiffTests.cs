using FluentAssertions;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class LineDiffTests
{
    private static string[] Render(string[] a, string[] b)
        => LineDiff.Compare(a, b).Select(o => $"{(o.Kind == LineDiffKind.Added ? '+' : o.Kind == LineDiffKind.Removed ? '-' : ' ')}{o.Text}").ToArray();

    [Fact] public void IdenticalInputIsAllUnchanged() => Render(["a", "b"], ["a", "b"]).Should().Equal(" a", " b");
    [Fact] public void AChangedLineIsARemovalThenAnAddition() => Render(["k=1", "z"], ["k=2", "z"]).Should().Equal("-k=1", "+k=2", " z");
    [Fact] public void RemovalsPrecedeAdditionsWithinOneRun() => Render(["a", "x", "y", "d"], ["a", "p", "d"]).Should().Equal(" a", "-x", "-y", "+p", " d");

    [Fact]
    public void TheScriptIsShortest()   // a shared line must not be dropped and re-added
        => LineDiff.Compare(["a", "b", "c"], ["b", "c", "d"]).Count(o => o.Kind != LineDiffKind.Unchanged).Should().Be(2);

    [Fact]
    public void IndicesPointIntoTheirOwnSide()
    {
        var ops = LineDiff.Compare(["a", "b"], ["a", "c"]);
        ops[0].Should().Be(new LineDiffOp(LineDiffKind.Unchanged, "a", 0, 0));
        ops[1].Should().Be(new LineDiffOp(LineDiffKind.Removed, "b", 1, -1));
        ops[2].Should().Be(new LineDiffOp(LineDiffKind.Added, "c", -1, 1));
    }

    [Fact]
    public void EmptySidesAreAllRemovedOrAllAdded()
    {
        Render([], ["a"]).Should().Equal("+a");
        Render(["a"], []).Should().Equal("-a");
        LineDiff.Compare([], []).Should().BeEmpty();
    }

    [Fact]
    public void TwentyThousandLinesCompareInLinearSpace()
    {
        // 20k lines each, fifty scattered edits: the old table would be int[20001, 20001] = 1.6 GB.
        var a = Enumerable.Range(0, 20_000).Select(i => $"line {i}").ToArray();
        var b = a.Select((l, i) => i % 400 == 7 ? $"{l} changed" : l).ToArray();
        var before = System.GC.GetAllocatedBytesForCurrentThread();
        var ops = LineDiff.Compare(a, b);
        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        ops.Count(o => o.Kind == LineDiffKind.Removed).Should().Be(50);
        ops.Count(o => o.Kind == LineDiffKind.Added).Should().Be(50);
        allocated.Should().BeLessThan(64L * 1024 * 1024);   // generous: the op list alone is ~20k structs
    }

    [Fact]
    public void SplitLinesNormalisesCrlfAndDropsTheTrailingEmptyLine()
    {
        LineDiff.SplitLines("a\r\nb\r\n").Should().Equal("a", "b");
        LineDiff.SplitLines("a\nb").Should().Equal("a", "b");
        LineDiff.SplitLines("").Should().BeEmpty();
    }

    [Fact]
    public void RepeatedBraceAndBlankLinesStillGiveAShortestScript()
    {
        string[] a = ["{", "", "}", "{", "", "}", "x"];
        string[] b = ["{", "", "}", "y", "{", "", "}"];
        var ops = LineDiff.Compare(a, b);
        ops.Count(o => o.Kind != LineDiffKind.Unchanged).Should().Be(2);
        ops.Where(o => o.Kind != LineDiffKind.Added).Select(o => o.Text).Should().Equal(a);
        ops.Where(o => o.Kind != LineDiffKind.Removed).Select(o => o.Text).Should().Equal(b);
    }

    [Fact]
    public void RandomInputsReplayToBothSidesAndMatchTheLcsLength()
    {
        var rng = new Random(12345);
        for (var round = 0; round < 300; round++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 25)).Select(_ => rng.Next(0, 4).ToString()).ToArray();
            var b = Enumerable.Range(0, rng.Next(0, 25)).Select(_ => rng.Next(0, 4).ToString()).ToArray();
            var ops = LineDiff.Compare(a, b);

            ops.Where(o => o.Kind != LineDiffKind.Added).Select(o => o.Text).Should().Equal(a);
            ops.Where(o => o.Kind != LineDiffKind.Removed).Select(o => o.Text).Should().Equal(b);
            ops.Where(o => o.Kind != LineDiffKind.Added).Select(o => o.IndexA).Should().Equal(Enumerable.Range(0, a.Length));
            ops.Where(o => o.Kind != LineDiffKind.Removed).Select(o => o.IndexB).Should().Equal(Enumerable.Range(0, b.Length));
            ops.Count(o => o.Kind == LineDiffKind.Unchanged).Should().Be(LcsLength(a, b));

            // Removals before additions inside every run of changes.
            for (var i = 1; i < ops.Count; i++)
                (ops[i - 1].Kind == LineDiffKind.Added && ops[i].Kind == LineDiffKind.Removed).Should().BeFalse();
        }
    }

    private static int LcsLength(string[] a, string[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        return t[0, 0];
    }
}
