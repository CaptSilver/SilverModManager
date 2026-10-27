namespace SilverModManager.Abstractions.Games.Merge;

public enum LineDiffKind
{
    Unchanged,
    Added,
    Removed,
}

/// <summary>One step of a line diff. <paramref name="IndexA"/> and <paramref name="IndexB"/> are the line's index in each side, or -1 for a side it is not on.</summary>
public readonly record struct LineDiffOp(LineDiffKind Kind, string Text, int IndexA, int IndexB);

/// <summary>
/// A line diff in linear space: Myers' O(ND) algorithm with the middle-snake refinement, so a 17k-line script costs
/// memory in proportion to its length rather than the length squared. Hunt-Szymanski is the usual alternative, but
/// its match lists blow up on source files full of repeated blank and brace lines.
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// A shortest edit script of <paramref name="a"/> against <paramref name="b"/>. Within one run of changes every
    /// Removed op comes before every Added op. Lines compare ordinally.
    /// </summary>
    public static IReadOnlyList<LineDiffOp> Compare(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Myers only asks whether two lines are equal, so compare small ints instead of strings.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ia = Intern(a, ids);
        var ib = Intern(b, ids);

        var removed = new bool[ia.Length];
        var added = new bool[ib.Length];
        Diff(ia, 0, ia.Length, ib, 0, ib.Length, removed, added, new Scratch());

        var ops = new List<LineDiffOp>(Math.Max(a.Count, b.Count));
        int x = 0, y = 0;
        while (x < ia.Length || y < ib.Length)
        {
            if (x < ia.Length && removed[x])
            {
                ops.Add(new LineDiffOp(LineDiffKind.Removed, a[x], x, -1));
                x++;
            }
            else if (y < ib.Length && added[y])
            {
                // Every Removed of the run has been emitted by now, because the check above wins while one is left.
                ops.Add(new LineDiffOp(LineDiffKind.Added, b[y], -1, y));
                y++;
            }
            else
            {
                ops.Add(new LineDiffOp(LineDiffKind.Unchanged, a[x], x, y));
                x++;
                y++;
            }
        }
        return ops;
    }

    /// <summary>
    /// Splits on "\n" after "\r\n" becomes "\n". A trailing line ending leaves no empty last line, and "" is no lines.
    /// </summary>
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // A trailing line ending leaves an empty last element that is not a line.
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static int[] Intern(IReadOnlyList<string> lines, Dictionary<string, int> ids)
    {
        var result = new int[lines.Count];
        for (var i = 0; i < result.Length; i++)
        {
            if (!ids.TryGetValue(lines[i], out var id))
            {
                id = ids.Count;
                ids.Add(lines[i], id);
            }
            result[i] = id;
        }
        return result;
    }

    /// <summary>The two furthest-reaching-endpoint arrays, allocated once and refilled by every level of the recursion.</summary>
    private sealed class Scratch
    {
        public int[] Forward = [];
        public int[] Backward = [];

        public void Ensure(int length)
        {
            if (Forward.Length >= length) return;
            Forward = new int[length];
            Backward = new int[length];
        }
    }

    private static void Diff(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, bool[] removed, bool[] added, Scratch scratch)
    {
        // Shared ends cost nothing; stripping them is what keeps a few scattered edits in a long file cheap.
        while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
        {
            aLo++;
            bLo++;
        }
        while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1])
        {
            aHi--;
            bHi--;
        }

        if (aLo == aHi)
        {
            for (var j = bLo; j < bHi; j++) added[j] = true;
            return;
        }
        if (bLo == bHi)
        {
            for (var i = aLo; i < aHi; i++) removed[i] = true;
            return;
        }

        var (x, y) = MiddleSnake(a, aLo, aHi, b, bLo, bHi, scratch);
        Diff(a, aLo, aLo + x, b, bLo, bLo + y, removed, added, scratch);
        Diff(a, aLo + x, aHi, b, bLo + y, bHi, removed, added, scratch);
    }

    /// <summary>
    /// Runs the forward and the reverse search together until they meet, and returns the point (relative to the
    /// ranges' starts) where the meeting happened. The ranges share no first or last line.
    /// </summary>
    private static (int X, int Y) MiddleSnake(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, Scratch scratch)
    {
        int n = aHi - aLo, m = bHi - bLo;
        // Each direction needs up to ceil((n + m) / 2) edits for the paths to cross; the extra slot of slack on
        // either side keeps the k +/- 1 reads inside the arrays.
        var maxD = (n + m + 1) / 2 + 1;
        var offset = maxD;
        var length = 2 * maxD + 2;
        scratch.Ensure(length);
        var v1 = scratch.Forward;
        var v2 = scratch.Backward;
        Array.Fill(v1, -1, 0, length);
        Array.Fill(v2, -1, 0, length);
        v1[offset + 1] = 0;
        v2[offset + 1] = 0;

        var delta = n - m;
        // An odd delta means the paths can only cross during a forward step, an even one during a reverse step.
        var front = delta % 2 != 0;
        int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;

        for (var d = 0; d <= maxD; d++)
        {
            for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
            {
                var k1Off = offset + k1;
                var x1 = k1 == -d || (k1 != d && v1[k1Off - 1] < v1[k1Off + 1]) ? v1[k1Off + 1] : v1[k1Off - 1] + 1;
                var y1 = x1 - k1;
                while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                {
                    x1++;
                    y1++;
                }
                v1[k1Off] = x1;
                if (x1 > n) k1End += 2;
                else if (y1 > m) k1Start += 2;
                else if (front)
                {
                    var k2Off = offset + delta - k1;
                    if (k2Off >= 0 && k2Off < length && v2[k2Off] != -1 && x1 >= n - v2[k2Off]) return (x1, y1);
                }
            }

            for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
            {
                var k2Off = offset + k2;
                var x2 = k2 == -d || (k2 != d && v2[k2Off - 1] < v2[k2Off + 1]) ? v2[k2Off + 1] : v2[k2Off - 1] + 1;
                var y2 = x2 - k2;
                while (x2 < n && y2 < m && a[aLo + n - x2 - 1] == b[bLo + m - y2 - 1])
                {
                    x2++;
                    y2++;
                }
                v2[k2Off] = x2;
                if (x2 > n) k2End += 2;
                else if (y2 > m) k2Start += 2;
                else if (!front)
                {
                    var k1Off = offset + delta - k2;
                    if (k1Off >= 0 && k1Off < length && v1[k1Off] != -1)
                    {
                        var x1 = v1[k1Off];
                        var y1 = offset + x1 - k1Off;
                        if (x1 >= n - x2) return (x1, y1);
                    }
                }
            }
        }

        // The two searches always meet within ceil((n + m) / 2) edits each; reaching here is a bug in the search above.
        throw new InvalidOperationException("The line diff's forward and reverse searches never met.");
    }
}
