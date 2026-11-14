using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// One file a mounted folder serves in place of the real folder's: a loadout file the mount shows, or a hidden path.
/// </summary>
/// <param name="Path">Where the mount presents the file.</param>
/// <param name="Identity">The served content.</param>
/// <param name="Size">The served content's size.</param>
/// <param name="BackingIdentity">
/// The real file this row shadows, or null when there was none. The row only holds while the real file is still
/// this one; a different file means something outside the app changed it.
/// </param>
/// <param name="Hidden">The mount shows nothing at this path although the real folder has a file there.</param>
/// <param name="MTime">The modification time the mount serves, when the game needs one.</param>
public readonly record struct MountManifestRow(
    GamePath Path,
    FileIdentity Identity,
    Size Size,
    FileIdentity? BackingIdentity,
    bool Hidden,
    DateTimeOffset? MTime)
{
    /// <summary>
    /// The real folder already holds the served bytes, so the row only carries <see cref="MTime"/>: the mount serves the
    /// real file under that time and stages nothing. A vanilla master can have no staged copy at all.
    /// </summary>
    public bool ServesTheRealFile => !Hidden && BackingIdentity is { } backing && backing.Equals(Identity);
}

/// <summary>
/// What a mounted folder serves, as the rows the synchronizer lays over the real folder's state when it builds a sync
/// tree. Nothing is written under a mount root on apply, so without the overlay the tree would compare the loadout
/// with files that are not there.
/// </summary>
public sealed record MountManifestView(GamePath Root, IReadOnlyList<MountManifestRow> Rows)
{
    public static MountManifestView Empty(GamePath root) => new(root, []);

    /// <summary>
    /// Replaces the real entry at a row's path, removes a hidden row's path, and drops a row whose backing identity
    /// differs from the real entry now there (so the real file is ingested), hidden or not. Every other path passes through.
    /// </summary>
    /// <remarks>
    /// A row with no real entry at its path is added, unless it is hidden or only retimes the real file (nothing serves
    /// it then, and the tree has to see the deletion). One pass over <paramref name="state"/>,
    /// which holds every file of the game, so the lookup is by path and not by scanning the rows.
    /// </remarks>
    public IEnumerable<PathPartPair> Overlay(IEnumerable<PathPartPair> state)
    {
        if (Rows.Count == 0)
        {
            foreach (var pair in state) yield return pair;
            yield break;
        }

        var rows = new Dictionary<GamePath, MountManifestRow>(Rows.Count);
        foreach (var row in Rows) rows[row.Path] = row;
        var seen = new HashSet<GamePath>();

        foreach (var pair in state)
        {
            if (!rows.TryGetValue(pair.Path, out var row))
            {
                yield return pair;
                continue;
            }

            seen.Add(pair.Path);

            // The row only describes what it shadows or hides. Any other file at the path arrived since, hidden or not.
            if (row.BackingIdentity is not { } backing || !backing.Equals(pair.Part.Identity))
            {
                yield return pair;
                continue;
            }

            if (row.Hidden) continue;

            yield return pair with { Part = Served(row, pair.Part) };
        }

        foreach (var row in Rows)
        {
            if (row.Hidden || row.ServesTheRealFile || seen.Contains(row.Path)) continue;
            yield return new PathPartPair(row.Path, Served(row, new SyncNodePart { Identity = row.Identity, Size = row.Size, LastModifiedTicks = 0 }));
        }
    }

    private static SyncNodePart Served(MountManifestRow row, SyncNodePart real) => new()
    {
        EntityId = real.EntityId,
        Identity = row.Identity,
        Size = row.Size,
        LastModifiedTicks = row.MTime?.UtcTicks ?? real.LastModifiedTicks,
    };
}
