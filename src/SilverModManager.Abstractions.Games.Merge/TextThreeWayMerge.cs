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

        /// <summary>Whether at most <paramref name="gap"/> vanilla lines lie between the two hunks.</summary>
        public bool IsWithin(Hunk k, int gap) => Math.Max(Start, k.Start) - Math.Min(End, k.End) <= gap;
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
        var lossesByMod = new Dictionary<string, List<LinesKept>>();
        foreach (var (text, mod) in contributors)
        {
            var own = HunksOf(vanilla, text, mod);
            var losses = lossesByMod[mod] = [];
            // A mod shipping byte-for-byte what a higher one ships fares exactly as that one did; replaying its hunks against a merge they already shaped only invites double counting.
            var twin = contributors.TakeWhile(c => c.ModName != mod).FirstOrDefault(c => c.Text == text);
            if (twin.ModName is not null)
            {
                walked += own.Count;
                foreach (var l in lossesByMod[twin.ModName])
                {
                    losses.Add(l with { DroppedMod = mod });
                    notes.Add(losses[^1]);
                }
                continue;
            }
            for (var i = 0; i < own.Count; i++)
            {
                var h = own[i];
                walked++;
                // The same edit from two mods is one edit. Applied hunks never conflict with each other, so an identical one is the only overlap there can be.
                if (applied.Any(k => k.SameEdit(h))) continue;
                var losing = applied.Where(h.Conflicts).ToList();
                if (losing.Count == 0 && applied.Any(k => IsNearbyRepeat(h, k))) continue;
                var near = applied.Where(k => h.IsWithin(k, ClusterGap)).ToList();
                if (TryMergeClusters(vanilla, contributors[0].ModName, own, i, applied, near) is { } consumed)
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
                var last = Math.Max(vanilla.Length, 1);
                foreach (var k in losing)
                {
                    losses.Add(new LinesKept(path, Math.Min(k.Start + 1, last), Math.Min(Math.Max(k.End, k.Start + 1), last), k.Owner, h.Owner));
                    notes.Add(losses[^1]);
                }
            }
        }

        // An insertion sorts before a change that starts at the same line, so it lands in front of it.
        applied = [.. applied.OrderBy(k => k.Start).ThenBy(k => k.End)];
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

    /// <summary>
    /// Tries the stretch with every nearby applied hunk, then with each applied mod's hunks alone: a third mod's
    /// unrelated change a line or two away would otherwise make the comparison fail.
    /// </summary>
    private static int? TryMergeClusters(string[] vanilla, string firstMod, List<Hunk> own, int at, List<Hunk> applied, List<Hunk> near)
    {
        if (near.Count == 0) return null;
        // Widest stretch first, then narrower: a later hunk of the lower mod, a line or two on, may belong to a different change.
        for (var limit = Math.Min(own.Count, at + MaxClusterHunks); limit > at; limit--)
        {
            if (TryMergeCluster(vanilla, firstMod, own, at, limit, applied, applied, near) is { } all) return all;
            foreach (var owner in near.Select(k => k.Owner).Distinct().ToList())
            {
                var theirs = near.Where(k => k.Owner == owner).ToList();
                if (applied.Any(k => k.Owner != owner) && TryMergeCluster(vanilla, firstMod, own, at, limit, applied, applied.Where(k => k.Owner == owner).ToList(), theirs) is { } one) return one;
            }
        }
        return null;
    }

    /// <summary>How many unchanged lines may separate two hunks of one change; see <see cref="TryMergeCluster"/>.</summary>
    private const int ClusterGap = 2;

    /// <summary>How many of the lower mod's consecutive hunks one comparison may span.</summary>
    private const int MaxClusterHunks = 32;

    /// <summary>A line made only of braces, brackets, semicolons, commas and blanks says nothing about which change it belongs to.</summary>
    private static bool IsTrivial(string line) => line.All(c => char.IsWhiteSpace(c) || "{}()[];,".Contains(c));

    /// <summary>The same non-trivial lines inserted a line or two apart: one change that two diffs placed differently.</summary>
    private static bool IsNearbyRepeat(Hunk h, Hunk k)
        => h.Owner != k.Owner && h.IsInsertion && k.IsInsertion && h.Lines.AsSpan().SequenceEqual(k.Lines)
            && !h.Lines.All(IsTrivial) && Math.Abs(h.Start - k.Start) <= ClusterGap + 1;

    /// <summary>
    /// The line diff places a change canonically only within a run of equal text. When a line is rewritten next to
    /// an inserted blank, or one copy of a repeated line is rewritten, two mods that made the same change can end up
    /// with hunks cut differently: one hunk on one side, a deletion plus an insertion a line apart on the other. So
    /// when a hunk lies within a couple of lines of what is applied, the stretch around them (grown over hunks at most <see cref="ClusterGap"/>
    /// lines apart) is compared as text. If the lower mod's version of that stretch is the applied one plus lines of
    /// its own, or is already contained in it, the two are one change: the lower mod's extra lines are added as an
    /// insertion hunk of their own and the applied hunks stay as they are. That only holds when the lines the two
    /// share include one of the applied mod's own that is not a blank or a lone brace; otherwise sharing proves
    /// nothing (a pure deletion against a rewrite "contains" it vacuously) and the overlap rule decides.
    /// </summary>
    /// <returns>How many of <paramref name="own"/>'s hunks from <paramref name="at"/> on were consumed, or null when the stretch is a real difference.</returns>
    private static int? TryMergeCluster(string[] vanilla, string firstMod, List<Hunk> own, int at, int limit, List<Hunk> applied, List<Hunk> candidates, List<Hunk> near)
    {

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
            for (; end < limit && own[end].Start <= to + ClusterGap; end++)
            {
                to = Math.Max(to, own[end].End);
                grew = true;
            }
            foreach (var k in candidates)
            {
                if (inside.Contains(k) || k.Start > to + ClusterGap || from > k.End + ClusterGap) continue;
                inside.Add(k);
                from = Math.Min(from, k.Start);
                to = Math.Max(to, k.End);
                grew = true;
            }
        }

        // Applied hunks the comparison left out must stay clear of the stretch, or the lines placed in it would ignore them.
        if (applied.Any(k => !inside.Contains(k) && (k.IsInsertion ? from <= k.Start && k.Start <= to : k.Start < to && from < k.End))) return null;

        var keptHunks = inside.OrderBy(k => k.Start).ThenBy(k => k.End).ToList();
        var kept = Stretch(vanilla, from, to, keptHunks, firstMod);
        var lowerHunks = own.GetRange(lo, end - lo);
        var lower = Stretch(vanilla, from, to, lowerHunks, firstMod);

        var prefix = 0;
        while (prefix < kept.Count && prefix < lower.Count && kept[prefix].Text == lower[prefix].Text) prefix++;
        var suffix = 0;
        while (suffix < kept.Count - prefix && suffix < lower.Count - prefix
            && kept[^(suffix + 1)].Text == lower[^(suffix + 1)].Text) suffix++;
        var keptMiddle = kept.Skip(prefix).Take(kept.Count - prefix - suffix).ToList();
        var lowerMiddle = lower.Skip(prefix).Take(lower.Count - prefix - suffix).ToList();

        var anchored = kept.Take(prefix).Concat(kept.Skip(kept.Count - suffix)).Any(l => l.Own && l.Owner != own[at].Owner && !IsTrivial(l.Text));
        if (!anchored) return null;
        // Only a mod's own lines count as its additions; a vanilla line it kept where the other mod removed one is a difference.
        if (!keptMiddle.All(l => l.Own) && keptMiddle.Count > 0) return null;
        if (keptMiddle.Count > 0 && lowerMiddle.Count > 0) return null;
        if (lowerMiddle.Count == 0) return end - at;
        if (!lowerMiddle.All(l => l.Own)) return null;

        // Where the lower mod's extra lines go, read off the applied side: inside the lines of one applied hunk, or at a vanilla boundary of their own.
        var texts = lower.Select(l => l.Text).ToList();
        var extraLines = lowerMiddle.Select(l => l.Text).ToArray();
        var extraOwners = lowerMiddle.Select(l => l.Owner).ToArray();
        Hunk? grown = null, replaced = null, extra = null;
        if (prefix > 0 && prefix < kept.Count && kept[prefix].Hunk is { } inner && kept[prefix - 1].Hunk == inner)
        {
            var offset = kept[prefix].Offset;
            replaced = inner;
            grown = inner with
            {
                Lines = [.. inner.Lines[..offset], .. extraLines, .. inner.Lines[offset..]],
                LineOwners = [.. inner.LineOwners[..offset], .. extraOwners, .. inner.LineOwners[offset..]],
            };
        }
        else
        {
            var at0 = prefix == 0 ? from : kept[prefix - 1].Hunk?.End ?? kept[prefix - 1].Pos + 1;
            extra = new Hunk(at0, at0, extraLines, extraOwners, own[at].Owner);
            if (keptHunks.Any(k => k.Start < at0 && at0 < k.End)) return null;
        }

        var rest = keptHunks.Where(k => k != replaced).Concat(grown is null ? [] : [grown]).ToList();
        // Two insertions at one point keep the order they were applied in, so the extra lines go before or after the applied ones; whichever reproduces the lower mod's text is right.
        var tie = extra is null ? null : rest.FirstOrDefault(k => k.IsInsertion && k.Start == extra.Start);
        foreach (var before in tie is null ? new[] { false } : new[] { false, true })
        {
            var candidate = extra is null ? rest : before ? [.. rest.TakeWhile(k => k != tie), extra, .. rest.SkipWhile(k => k != tie)] : [.. rest, extra];
            var ordered = candidate.OrderBy(k => k.Start).ThenBy(k => k.End).ToList();
            if (!Stretch(vanilla, from, to, ordered, firstMod).Select(l => l.Text).SequenceEqual(texts)) continue;
            if (replaced is not null) applied.Remove(replaced);
            if (extra is null) applied.Add(grown!);
            else if (before) applied.Insert(applied.IndexOf(tie!), extra);
            else applied.Add(extra);
            return end - at;
        }
        return null;
    }

    /// <summary>One line of a stretch: who contributed it, and the vanilla position an insertion before it would take.</summary>
    private readonly record struct StretchLine(string Text, bool Own, string Owner, int Pos, Hunk? Hunk, int Offset);

    /// <summary>The vanilla lines [from, to) with <paramref name="hunks"/> (sorted, inside the stretch) applied, each line marked with who contributed it.</summary>
    private static List<StretchLine> Stretch(string[] vanilla, int from, int to, List<Hunk> hunks, string firstMod)
    {
        var result = new List<StretchLine>();
        var next = from;
        foreach (var k in hunks)
        {
            for (; next < k.Start; next++) result.Add(new StretchLine(vanilla[next], false, firstMod, next, null, 0));
            for (var j = 0; j < k.Lines.Length; j++) result.Add(new StretchLine(k.Lines[j], true, k.LineOwners[j], k.Start, k, j));
            next = k.End;
        }
        for (; next < to; next++) result.Add(new StretchLine(vanilla[next], false, firstMod, next, null, 0));
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
