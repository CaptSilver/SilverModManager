namespace SilverModManager.DataModel.Undo;

/// <summary>
/// Picks a revision by the number the <c>loadout revisions</c> verb prints: the position in the list
/// ordered by timestamp, oldest first.
/// </summary>
internal static class RevisionSelection
{
    internal static bool TryGet(IEnumerable<LoadoutRevisionWithStats> revisions, int number, out LoadoutRevision revision)
    {
        var ordered = revisions.OrderBy(r => r.Revision.Timestamp).ToList();
        if (number < 0 || number >= ordered.Count)
        {
            revision = default;
            return false;
        }

        revision = ordered[number].Revision;
        return true;
    }
}
