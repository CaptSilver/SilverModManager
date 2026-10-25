using DynamicData.Binding;

namespace SilverModManager.App.UI.Helpers;

internal static class BoundListOrder
{
    /// <summary>
    /// Puts <paramref name="list"/> into the order <paramref name="order"/> gives, one Move per entry that is out of
    /// place. A list rebuilt with Clear and Add would make the view throw away and recreate every item, which shows as a
    /// blink.
    /// </summary>
    public static void MoveIntoOrder<T>(ObservableCollectionExtended<T> list, IComparer<T> order) where T : class
    {
        var sorted = list.OrderBy(item => item, order).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (ReferenceEquals(list[i], sorted[i])) continue;
            list.Move(list.IndexOf(sorted[i]), i);
        }
    }
}
