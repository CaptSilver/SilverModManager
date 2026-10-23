using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.CreationEngine.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;

namespace NexusMods.Games.CreationEngine;

/// <summary>
/// The note that the app replaced the game's plugin list. It is its own entity, one per sort order, so writing it
/// during Apply does not count as a change to the sort order a move in the UI checks against.
/// </summary>
internal static class PluginListNotices
{
    /// <summary>Every notice of the sort order, newest first.</summary>
    public static IReadOnlyList<PluginListNotice.ReadOnly> For(IDb db, SortOrderId sortOrderId) =>
        PluginListNotice.FindBySortOrder(db, sortOrderId).OrderByDescending(n => n.ReplacedAt).ToList();

    /// <summary>
    /// Makes the sort order's notice say <paramref name="summary"/>, creating it if there is none, or removes it
    /// when <paramref name="summary"/> is null.
    /// </summary>
    public static void Stage(IDb db, SortOrderId sortOrderId, string? summary, DateTimeOffset at, ITransaction tx)
    {
        var existing = For(db, sortOrderId);
        if (summary is null)
        {
            foreach (var notice in existing) tx.Delete(notice.Id, recursive: false);
            return;
        }

        if (existing.Count == 0)
        {
            _ = new PluginListNotice.New(tx)
            {
                SortOrderId = sortOrderId,
                Summary = summary,
                ReplacedAt = at,
            };
            return;
        }

        tx.Add(existing[0].Id, PluginListNotice.Summary, summary);
        tx.Add(existing[0].Id, PluginListNotice.ReplacedAt, at);
        foreach (var extra in existing.Skip(1)) tx.Delete(extra.Id, recursive: false);
    }
}
