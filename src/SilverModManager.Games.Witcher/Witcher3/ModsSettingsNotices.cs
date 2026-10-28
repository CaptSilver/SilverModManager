using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.Witcher.Witcher3;

/// <summary>
/// The note that the app replaced the mods.settings on disk. One per sort order; it stays until the user drags a
/// folder or flips a switch on the Load Order page, or a later copy on disk replaces it.
/// </summary>
internal static class ModsSettingsNotices
{
    /// <summary>Every notice of the sort order, newest first.</summary>
    public static IReadOnlyList<WitcherModsSettingsNotice.ReadOnly> For(IDb db, SortOrderId sortOrderId) =>
        WitcherModsSettingsNotice.FindBySortOrder(db, sortOrderId).OrderByDescending(n => n.ReplacedAt).ToList();

    /// <summary>The notices of every sort order of the loadout, newest first.</summary>
    public static IReadOnlyList<WitcherModsSettingsNotice.ReadOnly> For(IDb db, LoadoutId loadoutId) =>
        LoadoutSortOrder.FindByLoadout(db, loadoutId)
            .SelectMany(order => WitcherModsSettingsNotice.FindBySortOrder(db, order.SortOrderId))
            .OrderByDescending(n => n.ReplacedAt)
            .ToList();

    /// <summary>
    /// Makes the sort order's notice say <paramref name="summary"/>, creating it if there is none, or removes it when
    /// <paramref name="summary"/> is null.
    /// </summary>
    public static void Stage(IDb db, SortOrderId sortOrderId, string? summary, DateTimeOffset at, ITransaction tx)
    {
        var existing = For(db, sortOrderId);
        if (summary is null)
        {
            Retract(db, sortOrderId, tx);
            return;
        }

        if (existing.Count == 0)
        {
            _ = new WitcherModsSettingsNotice.New(tx)
            {
                SortOrderId = sortOrderId,
                Summary = summary,
                ReplacedAt = at,
            };
            return;
        }

        tx.Add(existing[0].Id, WitcherModsSettingsNotice.Summary, summary);
        tx.Add(existing[0].Id, WitcherModsSettingsNotice.ReplacedAt, at);
        foreach (var extra in existing.Skip(1)) tx.Delete(extra.Id, recursive: false);
    }

    /// <summary>Removes the sort order's notices: the user acted on the order, so the card has done its job.</summary>
    public static void Retract(IDb db, SortOrderId sortOrderId, ITransaction tx)
    {
        foreach (var notice in For(db, sortOrderId)) tx.Delete(notice.Id, recursive: false);
    }
}
