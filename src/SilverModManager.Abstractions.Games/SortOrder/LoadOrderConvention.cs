using System.ComponentModel;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// How every load order page presents its order: as the order mods load in, with the mod loaded last at the bottom,
/// and that mod wins. A variety whose file reads the other way round (REDmod's modlist.txt lets its first line win)
/// stores its own order and shows it in reverse, so the user never has to learn which end wins on which page.
/// </summary>
public static class LoadOrderConvention
{
    /// <summary>The phrase every page's title and winner tooltip use.</summary>
    public const string LastLoadedWins = "Last Loaded Wins";

    /// <summary>The override title of a page called <paramref name="pageName"/>.</summary>
    public static string Title(string pageName) => $"{pageName} - {LastLoadedWins}";

    /// <summary>The winner tooltip, <paramref name="explanation"/> saying what overrides what on this page.</summary>
    public static string WinnerToolTip(string explanation) => $"{LastLoadedWins}: {explanation}";

    /// <summary>
    /// The 1-based load position a row shows: 1 for the mod loaded first, <paramref name="lastIndex"/> + 1 for the
    /// winner. It follows the order's <paramref name="behavior"/>, not the page's sort direction, so reversing the page
    /// moves the rows without renumbering them.
    /// </summary>
    public static int LoadPosition(int sortIndex, int lastIndex, IndexOverrideBehavior behavior)
        => behavior == IndexOverrideBehavior.SmallerIndexWins ? lastIndex - sortIndex + 1 : sortIndex + 1;

    /// <summary>Whether a page sorted by <paramref name="direction"/> over an order where <paramref name="behavior"/> wins shows the winner in its first row.</summary>
    public static bool WinnerOnTop(ListSortDirection direction, IndexOverrideBehavior behavior)
        => direction == ListSortDirection.Ascending == (behavior == IndexOverrideBehavior.SmallerIndexWins);
}
