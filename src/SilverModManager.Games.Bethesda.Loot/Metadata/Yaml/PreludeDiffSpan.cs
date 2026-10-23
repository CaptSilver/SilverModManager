namespace NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// A line and column in the YAML text being parsed. The line is 1-based, the column 0-based, as
/// libloot stores them (it adds one when printing).
/// </summary>
internal readonly record struct SourcePosition(int Line, int Column)
{
    /// <summary>YamlDotNet marks are 1-based in both directions.</summary>
    public static SourcePosition From(YamlDotNet.Core.Mark mark) => new((int)mark.Line, (int)Math.Max(0, mark.Column - 1));
}

/// <summary>
/// Where a replacement prelude sits in the masterlist text that was parsed, so a position in that
/// text can be mapped back to the files the user can open. Follows libloot's <c>PreludeDiffSpan</c>.
/// </summary>
/// <param name="StartLine">The first line of the prelude in the parsed text.</param>
/// <param name="EndLine">The last line of the prelude in the parsed text.</param>
/// <param name="LinesAddedCount">The prelude's length in lines minus the replaced block's, negative if shorter.</param>
internal readonly record struct PreludeDiffSpan(int StartLine, int EndLine, int LinesAddedCount)
{
    /// <summary>
    /// Maps a position in the parsed text to the position in the original masterlist, or, if it is
    /// inside the prelude, to the position in the prelude file.
    /// </summary>
    public (SourcePosition Position, bool IsWithinPrelude) Adjust(SourcePosition position)
    {
        if (position.Line > EndLine)
        {
            return (position with { Line = Math.Max(0, position.Line - LinesAddedCount) }, false);
        }

        if (position.Line >= StartLine)
        {
            // The prelude is indented by two spaces; undo that for the column.
            return (new SourcePosition(position.Line - StartLine + 1, Math.Max(0, position.Column - 2)), true);
        }

        return (position, false);
    }
}
