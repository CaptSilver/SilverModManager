using SilverModManager.App.UI.Controls.GameWidget;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.MyGames;

internal static class MyGamesOrder
{
    /// <summary>
    /// Identifies an installation across sessions: the same game in another folder or store is a different tile.
    /// </summary>
    public static string KeyOf(GameInstallation installation) =>
        $"{installation.Game.GameId}|{installation.LocatorResult.Store.Value}|{installation.LocatorResult.Path}";

    /// <summary>
    /// Tiles in the saved order come first; the rest follow by game name, then folder. One instance lives as long as
    /// the view model; a new saved order updates its positions in place (<see cref="SetSavedOrder"/>).
    /// </summary>
    public sealed class Comparer : IComparer<IGameWidgetViewModel>
    {
        private Dictionary<string, int> _positions = new();

        public void SetSavedOrder(IReadOnlyList<string> savedOrder) => _positions = savedOrder
            .Select((key, index) => (key, index))
            .DistinctBy(pair => pair.key)
            .ToDictionary(pair => pair.key, pair => pair.index);

        public int Compare(IGameWidgetViewModel? x, IGameWidgetViewModel? y)
        {
            var xInstallation = x?.Installation;
            var yInstallation = y?.Installation;
            if (xInstallation is null) return yInstallation is null ? 0 : 1;
            if (yInstallation is null) return -1;

            var xPosition = _positions.GetValueOrDefault(KeyOf(xInstallation), int.MaxValue);
            var yPosition = _positions.GetValueOrDefault(KeyOf(yInstallation), int.MaxValue);
            if (xPosition != yPosition) return xPosition.CompareTo(yPosition);

            var byName = string.Compare(xInstallation.Game.DisplayName, yInstallation.Game.DisplayName, StringComparison.CurrentCulture);
            return byName != 0
                ? byName
                : string.CompareOrdinal(xInstallation.LocatorResult.Path.ToString(), yInstallation.LocatorResult.Path.ToString());
        }
    }
}
