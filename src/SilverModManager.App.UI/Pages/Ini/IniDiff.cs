using SilverModManager.Abstractions.Games.Merge;

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
        => LineDiff.Compare(LineDiff.SplitLines(before), LineDiff.SplitLines(after))
            .Select(op => new IniDiffLine(Enum.Parse<IniDiffKind>(op.Kind.ToString()), op.Text))
            .ToList();
}
