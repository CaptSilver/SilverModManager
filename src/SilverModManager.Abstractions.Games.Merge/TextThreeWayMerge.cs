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
    private sealed record Hunk(int Start, int End, string[] Lines, string Owner)
    {
        public bool IsInsertion => Start == End;

        public bool SameEdit(Hunk other) => Start == other.Start && End == other.End && Lines.AsSpan().SequenceEqual(other.Lines);

        /// <summary>
        /// Whether the two edits touch the same vanilla lines, or an insertion lands inside or at either edge of a change, or two different insertions share a point.
        /// An insertion on the edge counts because the diff picks the side of a changed line an added line goes on, and two mods' diffs can pick differently:
        /// applying both would repeat or reorder the higher mod's lines.
        /// </summary>
        public bool Conflicts(Hunk k)
        {
            if (!IsInsertion && !k.IsInsertion) return Start < k.End && k.Start < End;
            if (IsInsertion && k.IsInsertion) return Start == k.Start && !SameEdit(k);
            var (ins, change) = IsInsertion ? (this, k) : (k, this);
            return change.Start <= ins.Start && ins.Start <= change.End;
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

    /// <summary>How many vanilla lines may lie between two mods' identical hunks for them to count as one change.</summary>
    private const int ClusterGap = 2;

    private static (List<string> Lines, List<string> Owners, int Hunks) FoldThreeWay(string[] vanilla, IReadOnlyList<(string Text, string ModName)> contributors, string path, ICollection<IMergeNote> notes)
    {
        var applied = new List<Hunk>();
        var walked = 0;
        var last = Math.Max(vanilla.Length, 1);
        var reported = new HashSet<LinesKept>();
        // The note covers the dropped hunk's own lines: those are what the reader goes looking for in the lower mod.
        void Report(Hunk dropped, string winner)
        {
            var note = new LinesKept(path, Math.Min(dropped.Start + 1, last), Math.Min(Math.Max(dropped.End, dropped.Start + 1), last), winner, dropped.Owner);
            if (reported.Add(note)) notes.Add(note);
        }

        foreach (var (text, mod) in contributors)
        {
            var lost = new List<(Hunk Hunk, Hunk Winner)>();
            var clear = new List<Hunk>();
            // A higher hunk stands in for at most one hunk of each lower mod: a mod making the same insertion twice near it has a copy the output lacks.
            var used = new HashSet<Hunk>();
            foreach (var h in HunksOf(vanilla, text, mod))
            {
                walked++;
                // The diff places a change canonically only inside a run of equal text, so two mods making the same edit can land a line or two apart.
                // Nothing is lost by applying it once.
                var twins = applied.Where(k => !used.Contains(k) && IsSameChange(vanilla, h, k)).ToList();
                var losing = applied.Where(k => !twins.Contains(k) && (h.Conflicts(k) || (twins.Count == 0 && RepeatsNearby(h, k)))).ToList();
                if (losing.Count == 0 && twins.Count > 0)
                {
                    used.Add(twins[0]);
                    continue;
                }
                if (losing.Count == 0)
                {
                    clear.Add(h);
                    continue;
                }
                if (losing.FirstOrDefault(h.Conflicts) is { } beaten) lost.Add((h, beaten));
                foreach (var k in losing) Report(h, k.Owner);
            }
            // A hunk right beside one that lost, on either side, is most likely the rest of the same edit cut in two, and applying it alone could repeat lines the winner already has.
            foreach (var h in clear)
            {
                if (lost.FirstOrDefault(l => h.IsWithin(l.Hunk, ClusterGap)).Winner is { } beside) Report(h, beside.Owner);
                else applied.Add(h);
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
            foreach (var l in k.Lines) Emit(l, k.Owner);
            next = k.End;
        }
        for (; next < vanilla.Length; next++) Emit(vanilla[next], first);
        return (lines, owners, walked);
    }

    /// <summary>
    /// A hunk that adds a non-blank line another mod's hunk a line or two away also adds, without being the same change:
    /// the diffs cut one edit differently, and applying both would repeat the line. The lower hunk loses, with a note.
    /// </summary>
    private static bool RepeatsNearby(Hunk h, Hunk k)
        => h.Owner != k.Owner && h.IsWithin(k, ClusterGap) && h.Lines.Any(l => HasContent(l) && k.Lines.Contains(l));

    /// <summary>Two hunks from different mods that remove the same vanilla lines and add the same lines, within <see cref="ClusterGap"/> lines of each other. Unless the hunks are at the same place, a change made only of blank and brace lines is not matched: two mods adding or dropping one near each other are more likely two edits.</summary>
    private static bool IsSameChange(string[] vanilla, Hunk h, Hunk k)
        => h.Owner != k.Owner && (h.SameEdit(k) || (h.IsWithin(k, ClusterGap)
            && h.End - h.Start == k.End - k.Start
            && (h.Lines.Any(HasContent) || vanilla.AsSpan(h.Start, h.End - h.Start).ToArray().Any(HasContent))
            && vanilla.AsSpan(h.Start, h.End - h.Start).SequenceEqual(vanilla.AsSpan(k.Start, k.End - k.Start))
            && h.Lines.AsSpan().SequenceEqual(k.Lines)));

    /// <summary>A line with a letter or digit in it. Blanks and lone braces turn up everywhere, so seeing one twice says nothing about two mods making the same edit.</summary>
    private static bool HasContent(string line) => line.Any(char.IsLetterOrDigit);

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
