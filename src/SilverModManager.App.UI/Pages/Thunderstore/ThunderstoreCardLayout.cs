namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>One line of the card grid: the cards sitting side by side in it.</summary>
public sealed record ThunderstoreCardLine(IReadOnlyList<ThunderstorePackageRow> Cards);

/// <summary>
/// Arithmetic for the card grid. The grid is a virtualized list of lines, each holding as many cards as fit the
/// width: a list virtualizes by line, so only the lines on screen (and their cards and icons) exist.
/// </summary>
public static class ThunderstoreCardLayout
{
    /// <summary>Width of a card; the icon inside it is square and fills the card.</summary>
    public const double CardWidth = 240;

    /// <summary>Gap between neighbouring cards, horizontally and vertically.</summary>
    public const double Gap = 16;

    public static int ColumnsFor(double availableWidth)
        => Math.Max(1, (int)((availableWidth + Gap) / (CardWidth + Gap)));

    public static IReadOnlyList<ThunderstoreCardLine> Chunk(IReadOnlyList<ThunderstorePackageRow> rows, int columns)
    {
        var lines = new List<ThunderstoreCardLine>((rows.Count + columns - 1) / columns);
        for (var i = 0; i < rows.Count; i += columns)
        {
            var cards = new ThunderstorePackageRow[Math.Min(columns, rows.Count - i)];
            for (var j = 0; j < cards.Length; j++) cards[j] = rows[i + j];
            lines.Add(new ThunderstoreCardLine(cards));
        }
        return lines;
    }
}
