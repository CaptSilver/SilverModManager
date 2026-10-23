using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.Larian.BaldursGate3.ModList;

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

    /// <summary>How many times the game threw the mod list away; zero before it did.</summary>
    public static int ResetCount(IDb db, SortOrderId sortOrderId) =>
        Find(db, sortOrderId) is { } notice && notice.ResetCount.HasValue ? notice.ResetCount.Value : 0;

    /// <summary>Whether the game has thrown the mod list away often enough that the app leaves the file to it.</summary>
    public static bool IsHeld(IDb db, SortOrderId sortOrderId) => ResetCount(db, sortOrderId) >= HoldAfterResets;

    /// <summary>The number of resets after which the app stops putting its list back.</summary>
    public const int HoldAfterResets = 2;

    /// <summary>
    /// The paks that went into the list most recently: those written last and not the time before, or all of the
    /// last list when nothing is new, so the likely cause of a reset is always named.
    /// </summary>
    public static IReadOnlyList<string> NewlyActive(IDb db, SortOrderId sortOrderId)
    {
        if (Find(db, sortOrderId) is not { } notice || !notice.LastWrittenPaks.HasValue) return [];
        var last = Split(notice.LastWrittenPaks.Value);
        var previous = notice.PreviousWrittenPaks.HasValue
            ? Split(notice.PreviousWrittenPaks.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var added = last.Where(name => !previous.Contains(name)).ToList();
        return added.Count > 0 ? added : last;
    }

    /// <summary>
    /// Counts one more reset and rewrites the note to say what the game did and which paks were new in the list.
    /// The notice must exist: a reset is only found against a list the app wrote.
    /// </summary>
    public static void StageReset(IDb db, SortOrderId sortOrderId, IReadOnlyList<string> newlyActive, DateTimeOffset at, ITransaction tx)
    {
        var notice = Find(db, sortOrderId) ?? throw new InvalidOperationException("A reset needs a list the app wrote, so the notice exists.");
        var count = (notice.ResetCount.HasValue ? notice.ResetCount.Value : 0) + 1;
        var caused = newlyActive.Count == 0 ? "" : $" The paks that were newly in the list: {string.Join(", ", newlyActive)}.";
        var summary = count >= HoldAfterResets
            ? $"The game threw the mod list away {count} times, so the app is leaving the file to it.{caused}"
            : $"The game threw the mod list away and the app put it back.{caused}";

        tx.Add(notice.Id, Bg3ModListNotice.ResetCount, count);
        tx.Add(notice.Id, Bg3ModListNotice.Summary, summary);
        tx.Add(notice.Id, Bg3ModListNotice.ReplacedAt, at);
    }

    /// <summary>
    /// Records that <paramref name="names"/> is what is about to be written, and moves the list that was last
    /// written to the one before it. Creates the notice when the sort order has none. A plan equal to the last
    /// one stages nothing, so the list before it keeps naming the paks that arrived with the last change, which
    /// is what reset detection and the reset summary look at. Returns whether anything was staged.
    /// </summary>
    public static bool StagePlan(IDb db, SortOrderId sortOrderId, IReadOnlyList<string> names, DateTimeOffset at, ITransaction tx)
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
            return true;
        }

        if (notice.LastWrittenPaks.HasValue && notice.LastWrittenPaks.Value == joined) return false;

        if (notice.LastWrittenPaks.HasValue) tx.Add(notice.Id, Bg3ModListNotice.PreviousWrittenPaks, notice.LastWrittenPaks.Value);
        tx.Add(notice.Id, Bg3ModListNotice.LastWrittenPaks, joined);
        return true;
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

    /// <summary>
    /// Whether the notice holds anything besides the note, which clearing the note must not lose. An empty
    /// written list counts: it is what the next plan is compared against, and losing it would recreate the notice
    /// on every Apply of a loadout with no paks.
    /// </summary>
    public static bool HoldsMoreThanTheNote(Bg3ModListNotice.ReadOnly notice) =>
        notice.LastWrittenPaks.HasValue
        || notice.PreviousWrittenPaks.HasValue
        || notice.ResetCount.HasValue;

    private static string[] Split(string joined) => joined.Length == 0 ? [] : joined.Split(Separator);
}
