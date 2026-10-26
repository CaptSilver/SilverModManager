namespace SilverModManager.App.UI.Pages.Ini;

public enum IniDiffKind
{
    Unchanged,
    Added,
    Removed,
}

public readonly record struct IniDiffLine(IniDiffKind Kind, string Text);

/// <summary>A line by line comparison of two INI texts, for showing what the tweaks changed in a file.</summary>
public static class IniDiff
{
    /// <summary>
    /// The lines of <paramref name="before"/> and <paramref name="after"/> in order, each marked unchanged, removed
    /// from <paramref name="before"/> or added in <paramref name="after"/>. Line endings are not compared.
    /// </summary>
    public static IReadOnlyList<IniDiffLine> Lines(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);

        // Longest common subsequence table; INI files are a few hundred lines, so the quadratic table is small.
        var common = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);

        var lines = new List<IniDiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                lines.Add(new IniDiffLine(IniDiffKind.Unchanged, a[x]));
                x++;
                y++;
            }
            else if (common[x + 1, y] >= common[x, y + 1])
            {
                lines.Add(new IniDiffLine(IniDiffKind.Removed, a[x++]));
            }
            else
            {
                lines.Add(new IniDiffLine(IniDiffKind.Added, b[y++]));
            }
        }
        while (x < a.Length) lines.Add(new IniDiffLine(IniDiffKind.Removed, a[x++]));
        while (y < b.Length) lines.Add(new IniDiffLine(IniDiffKind.Added, b[y++]));
        return lines;
    }

    private static string[] Split(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // A trailing line ending leaves an empty last element that is not a line.
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
