using System.Collections.ObjectModel;

namespace SilverModManager.App.UI;

public static class ListReconcile
{
    /// <summary>
    /// Brings <paramref name="shown"/> to <paramref name="wanted"/> without replacing the list, so a bound list view
    /// keeps its scroll position and the lines it has built. Both keep the rows in one order, so what differs is rows
    /// to drop and rows to put in between the ones that stay.
    /// </summary>
    public static void Reconcile<T>(ObservableCollection<T> shown, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(shown[i])) shown.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= shown.Count || !ReferenceEquals(shown[i], wanted[i])) shown.Insert(i, wanted[i]);
        }
    }
}
