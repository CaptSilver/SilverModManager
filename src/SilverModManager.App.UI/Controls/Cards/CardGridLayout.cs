namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>One line of the card grid: the cards sitting side by side in it.</summary>
public sealed record CardLine(IReadOnlyList<object> Cards);

/// <summary>
/// Arithmetic for the card grid. The grid is a virtualized list of lines, each holding as many cards as fit the
/// width: a list virtualizes by line, so only the lines on screen (and their cards and thumbnails) exist.
/// </summary>
public static class CardGridLayout
{
    /// <summary>Width of a card; the thumbnail inside it fills the card minus its padding.</summary>
    public const double CardWidth = 240;

    /// <summary>Gap between neighbouring cards, horizontally and vertically.</summary>
    public const double Gap = 16;

    public static int ColumnsFor(double availableWidth)
        => Math.Max(1, (int)((availableWidth + Gap) / (CardWidth + Gap)));

    public static IReadOnlyList<CardLine> Chunk(IReadOnlyList<object> items, int columns) => Chunk(items, columns, startAt: 0);

    /// <summary>Lines of <paramref name="columns"/> cards from <paramref name="items"/>, beginning with the item at <paramref name="startAt"/>.</summary>
    public static IReadOnlyList<CardLine> Chunk(IReadOnlyList<object> items, int columns, int startAt)
    {
        var lines = new List<CardLine>((items.Count - startAt + columns - 1) / columns);
        for (var i = startAt; i < items.Count; i += columns)
        {
            var cards = new object[Math.Min(columns, items.Count - i)];
            for (var j = 0; j < cards.Length; j++) cards[j] = items[i + j];
            lines.Add(new CardLine(cards));
        }
        return lines;
    }
}
