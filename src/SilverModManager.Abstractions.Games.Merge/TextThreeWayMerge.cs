using System.Text;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>Both mods changed the same lines; the higher-priority mod's were kept.</summary>
/// <param name="FirstLine">1-based, in the vanilla file when there is one, else in the preferred file.</param>
public sealed record LinesKept(string Path, int FirstLine, int LastLine, string KeptMod, string DroppedMod) : IMergeNote
{
    public string Describe() => $"lines {FirstLine}–{LastLine}: both mods changed them; kept {KeptMod}'s";

    public string Summary() => $"both {KeptMod} and {DroppedMod} change `{Path}` lines {FirstLine}–{LastLine}; {KeptMod}'s are used.";

    public MergeLoss? Loss => MergeLoss.Between(DroppedMod, KeptMod);
}

/// <summary>No vanilla copy of the file exists, so the merge was two-way and the higher-priority mod wins every difference.</summary>
public sealed record NoVanillaBase(string Path) : IVanillaSourceNote
{
    public string Describe() => $"{Path}: no vanilla copy; merged two-way, the higher-priority mod wins every difference";
}

/// <summary>The installed vanilla copy differs from the store's file list, so it was not used as the base.</summary>
public sealed record VanillaBaseUntrusted(string Path, string Reason) : IVanillaSourceNote
{
    public string Describe() => $"{Path}: the game's own copy was not used as the base ({Reason}); merged two-way";
}

/// <summary>
/// A line-level diff3 where the higher-priority mod wins overlaps. The merge is the vanilla lines plus a sorted list
/// of applied hunks, each owned by the mod that contributed it, and is never re-diffed: that is what lets every
/// <see cref="LinesKept"/> name the mod whose lines are really in the output.
/// </summary>
public sealed class TextThreeWayMerge : IMergeStrategy
{
    private static readonly UTF8Encoding Strict = new(false, true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static readonly TextThreeWayMerge Instance = new();

    public string Name => "text-3way";

    /// <summary>Two-contributor convenience over <see cref="Fold(string?, IReadOnlyList{ValueTuple{string, string}}, string, ICollection{IMergeNote})"/>: the preferred side's line ending and trailing-newline state win.</summary>
    public static string Merge(string? vanilla, string preferred, string other, string preferredName, string otherName, string path, ICollection<IMergeNote> notes)
        => Fold(vanilla, [(preferred, preferredName), (other, otherName)], path, notes).Text;

    public MergeFoldResult Fold(IReadOnlyList<MergeInput> contributors, ReadOnlySpan<byte> vanilla, bool hasVanilla, string path, ICollection<IMergeNote> notes)
    {
        var texts = new List<(string Text, string ModName)>(contributors.Count);
        foreach (var c in contributors) texts.Add((Decode(c.Bytes, c.ModName), c.ModName));
        var vanillaText = hasVanilla ? Decode(vanilla, "the game's copy") : null;

        var (text, _, hunks) = Fold(vanillaText, texts, path, notes);
        var body = Strict.GetBytes(text);
        var keepBom = contributors.Count > 0 && contributors[0].Bytes.AsSpan().StartsWith(Bom);
        return new MergeFoldResult(keepBom ? [.. Bom, .. body] : body, hunks);
    }

    private static string Decode(ReadOnlySpan<byte> bytes, string who)
    {
        if (bytes.StartsWith(Bom)) bytes = bytes[Bom.Length..];
        try
        {
            return Strict.GetString(bytes);
        }
        catch (ArgumentException)
        {
            throw new MergeInputException($"{who} is not UTF-8 text");
        }
    }

    /// <summary>The string-level fold. <c>Owners[i]</c> is the mod that contributed merged line i; a vanilla line is owned by the first contributor.</summary>
    internal static (string Text, IReadOnlyList<string> Owners, int Hunks) Fold(string? vanilla, IReadOnlyList<(string Text, string ModName)> contributors, string path, ICollection<IMergeNote> notes)
    {
        if (contributors.Count == 0) return ("", [], 0);
        var (lines, owners, hunks) = vanilla is null
            ? FoldTwoWay(contributors, path, notes)
            : FoldThreeWay(LineDiff.SplitLines(vanilla), contributors, path, notes);
        return (Join(lines, contributors), owners, hunks);
    }

    /// <summary>A change to vanilla lines [Start, End) replacing them with Lines; Start == End is an insertion before line Start.</summary>
    private sealed record Hunk(int Start, int End, string[] Lines, string[] LineOwners, string Owner)
    {
        public Hunk(int start, int end, string[] lines, string owner)
            : this(start, end, lines, [.. lines.Select(_ => owner)], owner)
        {
        }

        public bool IsInsertion => Start == End;

        public bool SameEdit(Hunk other) => Start == other.Start && End == other.End && Lines.AsSpan().SequenceEqual(other.Lines);

        /// <summary>Whether the two edits touch the same vanilla lines, or an insertion lands strictly inside a change, or two different insertions share a point.</summary>
        public bool Conflicts(Hunk k)
        {
            if (!IsInsertion && !k.IsInsertion) return Start < k.End && k.Start < End;
            if (IsInsertion && k.IsInsertion) return Start == k.Start && !SameEdit(k);
            var (ins, change) = IsInsertion ? (this, k) : (k, this);
            return change.Start < ins.Start && ins.Start < change.End;
        }

        public bool Touches(Hunk k) => Start <= k.End && k.Start <= End;
    }

    private static List<Hunk> HunksOf(string[] vanilla, string text, string owner)
    {
        var hunks = new List<Hunk>();
        var ops = LineDiff.Compare(vanilla, LineDiff.SplitLines(text));
        var pos = 0;   // vanilla lines consumed so far
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i].Kind == LineDiffKind.Unchanged)
            {
                pos++;
                i++;
                continue;
            }
            var start = pos;
            var added = new List<string>();
            for (; i < ops.Count && ops[i].Kind != LineDiffKind.Unchanged; i++)
            {
                if (ops[i].Kind == LineDiffKind.Removed) pos++;
                else added.Add(ops[i].Text);
            }
            hunks.Add(new Hunk(start, pos, [.. added], owner));
        }
        return hunks;
    }

    private static (List<string> Lines, List<string> Owners, int Hunks) FoldThreeWay(string[] vanilla, IReadOnlyList<(string Text, string ModName)> contributors, string path, ICollection<IMergeNote> notes)
    {
        var applied = new List<Hunk>();
        var walked = 0;
        foreach (var (text, mod) in contributors)
        {
            var own = HunksOf(vanilla, text, mod);
            for (var i = 0; i < own.Count; i++)
            {
                var h = own[i];
                walked++;
                // The same edit from two mods is one edit. Applied hunks never conflict with each other, so an identical one is the only overlap there can be.
                if (applied.Any(k => k.SameEdit(h))) continue;
                var losing = applied.Where(h.Conflicts).ToList();
                var near = applied.Where(h.Touches).ToList();
                if (near.Count > 0 && TryMergeCluster(vanilla, contributors[0].ModName, own, i, applied, near) is { } consumed)
                {
                    walked += consumed - 1;
                    i += consumed - 1;
                    continue;
                }
                if (losing.Count == 0)
                {
                    applied.Add(h);
                    continue;
                }
                foreach (var k in losing)
                    notes.Add(new LinesKept(path, k.Start + 1, Math.Max(k.End, k.Start + 1), k.Owner, h.Owner));
            }
        }

        // An insertion sorts before a change that starts at the same line, so it lands in front of it.
        applied.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : x.End.CompareTo(y.End));
        var first = contributors[0].ModName;
        List<string> lines = [], owners = [];
        var next = 0;
        void Emit(string line, string owner)
        {
            lines.Add(line);
            owners.Add(owner);
        }
        foreach (var k in applied)
        {
            for (; next < k.Start; next++) Emit(vanilla[next], first);
            for (var j = 0; j < k.Lines.Length; j++) Emit(k.Lines[j], k.LineOwners[j]);
            next = k.End;
        }
        for (; next < vanilla.Length; next++) Emit(vanilla[next], first);
        return (lines, owners, walked);
    }

    /// <summary>How many unchanged lines may separate two hunks of one change; see <see cref="TryMergeCluster"/>.</summary>
    private const int ClusterGap = 2;

    /// <summary>
    /// The line diff places a change canonically only within a run of equal text. When a line is rewritten next to
    /// an inserted blank, or one copy of a repeated line is rewritten, two mods that made the same change can end up
    /// with hunks cut differently: one hunk on one side, a deletion plus an insertion a line apart on the other. So
    /// when a hunk touches what is applied, the stretch around them (grown over hunks at most <see cref="ClusterGap"/>
    /// lines apart) is compared as text. If the lower mod's version of that stretch is the applied one plus
    /// additions of its own, or is already contained in it, the two are one change: the additions are kept and the
    /// shared lines stay with the mod that had them. Anything else is left to the overlap rule.
    /// </summary>
    /// <returns>How many of <paramref name="own"/>'s hunks from <paramref name="at"/> on were consumed, or null when the stretch is a real difference.</returns>
    private static int? TryMergeCluster(string[] vanilla, string firstMod, List<Hunk> own, int at, List<Hunk> applied, List<Hunk> near)
    {
        var keptOwner = near[0].Owner;
        if (near.Any(k => k.Owner != keptOwner)) return null;

        var from = Math.Min(own[at].Start, near.Min(k => k.Start));
        var to = Math.Max(own[at].End, near.Max(k => k.End));
        var lo = at;
        var end = at + 1;
        var inside = new HashSet<Hunk>(near);
        for (bool grew = true; grew;)
        {
            grew = false;
            // Earlier hunks of this mod belong to the comparison too; the ones that were taken or skipped already agree with what is applied.
            for (; lo > 0 && own[lo - 1].End + ClusterGap >= from; lo--)
            {
                from = Math.Min(from, own[lo - 1].Start);
                grew = true;
            }
            for (; end < own.Count && own[end].Start <= to + ClusterGap; end++)
            {
                to = Math.Max(to, own[end].End);
                grew = true;
            }
            foreach (var k in applied)
            {
                if (inside.Contains(k) || k.Start > to + ClusterGap || from > k.End + ClusterGap) continue;
                if (k.Owner != keptOwner)
                {
                    // Another mod's hunk close by is none of this comparison's business, unless it sits in the stretch.
                    if (k.Touches(new Hunk(from, to, [], ""))) return null;
                    continue;
                }
                inside.Add(k);
                from = Math.Min(from, k.Start);
                to = Math.Max(to, k.End);
                grew = true;
            }
        }
        var keptHunks = inside.OrderBy(k => k.Start).ThenBy(k => k.End).ToList();
        var kept = Stretch(vanilla, from, to, keptHunks, firstMod);
        var lower = Stretch(vanilla, from, to, own.GetRange(lo, end - lo), firstMod);

        var prefix = 0;
        while (prefix < kept.Count && prefix < lower.Count && kept[prefix].Text == lower[prefix].Text) prefix++;
        var suffix = 0;
        while (suffix < kept.Count - prefix && suffix < lower.Count - prefix
            && kept[^(suffix + 1)].Text == lower[^(suffix + 1)].Text) suffix++;
        var keptMiddle = kept.Count - prefix - suffix;
        var lowerMiddle = lower.GetRange(prefix, lower.Count - prefix - suffix);

        // Only a mod's own lines count as its additions; a vanilla line it kept where the other mod removed one is a difference.
        if (lowerMiddle.Count == 0 && kept.Skip(prefix).Take(keptMiddle).All(l => l.Own))
            return end - at;
        if (keptMiddle == 0 && lowerMiddle.All(l => l.Own))
        {
            var merged = lower.Select((l, j) => j >= prefix && j < prefix + lowerMiddle.Count ? l : kept[j < prefix ? j : kept.Count - (lower.Count - j)]).ToList();
            foreach (var k in keptHunks) applied.Remove(k);
            applied.Add(new Hunk(from, to, [.. merged.Select(l => l.Text)], [.. merged.Select(l => l.Owner)], keptOwner));
            return end - at;
        }
        return null;
    }

    private readonly record struct StretchLine(string Text, bool Own, string Owner);

    /// <summary>The vanilla lines [from, to) with <paramref name="hunks"/> (sorted, inside the stretch) applied, each line marked with who contributed it.</summary>
    private static List<StretchLine> Stretch(string[] vanilla, int from, int to, List<Hunk> hunks, string firstMod)
    {
        var result = new List<StretchLine>();
        var next = from;
        foreach (var k in hunks)
        {
            for (; next < k.Start; next++) result.Add(new StretchLine(vanilla[next], false, firstMod));
            for (var j = 0; j < k.Lines.Length; j++) result.Add(new StretchLine(k.Lines[j], true, k.LineOwners[j]));
            next = k.End;
        }
        for (; next < to; next++) result.Add(new StretchLine(vanilla[next], false, firstMod));
        return result;
    }

    private static (List<string> Lines, List<string> Owners, int Hunks) FoldTwoWay(IReadOnlyList<(string Text, string ModName)> contributors, string path, ICollection<IMergeNote> notes)
    {
        var (firstText, kept) = contributors[0];
        var first = LineDiff.SplitLines(firstText);
        var walked = 0;
        foreach (var (text, mod) in contributors.Skip(1))
        {
            var ops = LineDiff.Compare(first, LineDiff.SplitLines(text));
            for (var i = 0; i < ops.Count;)
            {
                if (ops[i].Kind == LineDiffKind.Unchanged)
                {
                    i++;
                    continue;
                }
                int firstLine = -1, lastLine = -1, at = -1;
                for (; i < ops.Count && ops[i].Kind != LineDiffKind.Unchanged; i++)
                {
                    if (ops[i].Kind != LineDiffKind.Removed) continue;
                    if (firstLine < 0) firstLine = ops[i].IndexA + 1;
                    lastLine = ops[i].IndexA + 1;
                }
                if (firstLine < 0)
                {
                    // An insertion sits before the next shared line, or after the last one.
                    at = i < ops.Count ? ops[i].IndexA : first.Length;
                    firstLine = lastLine = Math.Clamp(at + 1, 1, Math.Max(first.Length, 1));
                }
                walked++;
                notes.Add(new LinesKept(path, firstLine, lastLine, kept, mod));
            }
        }
        return ([.. first], [.. first.Select(_ => kept)], walked);
    }

    /// <summary>Joins with the first contributor's line ending and trailing-newline state; when that text is empty it has neither, so the next non-empty contributor's are used.</summary>
    private static string Join(List<string> lines, IReadOnlyList<(string Text, string ModName)> contributors)
    {
        if (lines.Count == 0) return "";
        var model = contributors.Select(c => c.Text).FirstOrDefault(t => t.Length > 0) ?? "";
        var newline = model.IndexOf('\n') is var nl and > 0 && model[nl - 1] == '\r' ? "\r\n" : "\n";
        var text = string.Join(newline, lines);
        return model.EndsWith('\n') ? text + newline : text;
    }
}
