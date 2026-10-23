using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Games.Larian.BaldursGate3.ModList;

/// <summary>
/// The one <see cref="Bg3ModListNotice"/> of a pak sort order. It holds the note that the game's mod list was
/// replaced (an empty summary means there is none) and the pak lists the app wrote, so the entity outlives the
/// note.
/// </summary>
public static class Bg3ModListNotices
{
    private const char Separator = '\n';

    public static Bg3ModListNotice.ReadOnly? Find(IDb db, SortOrderId sortOrderId) =>
        Bg3ModListNotice.FindBySortOrder(db, sortOrderId).Cast<Bg3ModListNotice.ReadOnly?>().FirstOrDefault();

    /// <summary>The paks the app wrote to the mod list last, in order; empty before anything was written.</summary>
    public static IReadOnlyList<string> LastWritten(IDb db, SortOrderId sortOrderId) =>
        Find(db, sortOrderId) is { } notice && notice.LastWrittenPaks.HasValue ? Split(notice.LastWrittenPaks.Value) : [];

    /// <summary>
    /// Records that <paramref name="names"/> is what is about to be written, and moves the list that was last
    /// written to the one before it. Creates the notice when the sort order has none.
    /// </summary>
    public static void StagePlan(IDb db, SortOrderId sortOrderId, IReadOnlyList<string> names, DateTimeOffset at, ITransaction tx)
    {
        var joined = string.Join(Separator, names);
        if (Find(db, sortOrderId) is not { } notice)
        {
            _ = new Bg3ModListNotice.New(tx)
            {
                SortOrderId = sortOrderId,
                Summary = "",
                ReplacedAt = at,
                LastWrittenPaks = joined,
            };
            return;
        }

        if (notice.LastWrittenPaks.HasValue) tx.Add(notice.Id, Bg3ModListNotice.PreviousWrittenPaks, notice.LastWrittenPaks.Value);
        tx.Add(notice.Id, Bg3ModListNotice.LastWrittenPaks, joined);
    }

    /// <summary>
    /// Makes the notice say <paramref name="summary"/>, or say nothing when it is null. Does nothing for a sort
    /// order with no notice and nothing to say.
    /// </summary>
    public static void StageSummary(IDb db, SortOrderId sortOrderId, string? summary, DateTimeOffset at, ITransaction tx)
    {
        var notice = Find(db, sortOrderId);
        if (notice is null)
        {
            if (summary is null) return;
            _ = new Bg3ModListNotice.New(tx)
            {
                SortOrderId = sortOrderId,
                Summary = summary,
                ReplacedAt = at,
            };
            return;
        }

        if (summary is null)
        {
            if (notice.Value.Summary != "") tx.Add(notice.Value.Id, Bg3ModListNotice.Summary, "");
            return;
        }

        tx.Add(notice.Value.Id, Bg3ModListNotice.Summary, summary);
        tx.Add(notice.Value.Id, Bg3ModListNotice.ReplacedAt, at);
    }

    /// <summary>Whether the notice holds anything besides the note, which clearing the note must not lose.</summary>
    public static bool HoldsMoreThanTheNote(Bg3ModListNotice.ReadOnly notice) =>
        notice.LastWrittenPaks is { HasValue: true, Value.Length: > 0 }
        || notice.PreviousWrittenPaks is { HasValue: true, Value.Length: > 0 }
        || notice.ResetCount.HasValue;

    private static string[] Split(string joined) => joined.Length == 0 ? [] : joined.Split(Separator);
}
