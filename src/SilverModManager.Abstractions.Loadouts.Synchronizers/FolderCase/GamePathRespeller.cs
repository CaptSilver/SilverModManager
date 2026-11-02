using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;

/// <summary>
/// Rewrites the paths the database records for a game so each folder has the spelling <see cref="FolderSpellings"/>
/// chose and the disk has. Rows differing from the disk in case would be written, and deleted, at the row's spelling,
/// recreating the twin folder the merge removed.
/// </summary>
/// <remarks>
/// <para>
/// The attribute that holds a path compares text without regard to case, so one transaction that swaps a path for its
/// re-cased twin cancels itself out. Rows that other rows reference (the loadout's items, the pins, the game-installed
/// files and claims) therefore pass through a marked spelling: a first commit writes the canonical path plus
/// <see cref="Marker"/>, a second the canonical path. Both commits of a chunk run before the next chunk starts, so at
/// most one chunk is ever marked, and <see cref="GameInstallMetadata.RespellPending"/> is set for the whole run so a crash
/// is found without scanning every row. Each commit is a loadout revision; anything watching can see paths ending in the
/// marker between the two.
/// </para>
/// <para>
/// The disk-state rows are referenced by nothing, so each is recreated in one transaction: no marker, one commit.
/// </para>
/// <para>
/// Left as they are: <c>LoadoutIniState.Path</c> and <c>DeploymentPlanEntry.Root</c>. Their lookups compare without
/// regard to case and those paths are never written to disk.
/// </para>
/// </remarks>
public static class GamePathRespeller
{
    /// <summary>Suffix a path carries between the two commits of a rewrite.</summary>
    public const string Marker = "~recase";

    private readonly record struct Change(PathRow Row, GamePathParentAttribute Attribute, string Canonical);

    private readonly record struct Sources(List<PathRow> Targets, List<PathRow> Pins, List<PathRow> Installed, List<PathRow> Claims, List<PathRow> Disk);

    /// <summary>
    /// Finishes rows a crashed run left marked. Does nothing, and reads nothing, unless
    /// <see cref="GameInstallMetadata.RespellPending"/> is set. Returns the number of rows finished.
    /// </summary>
    public static async ValueTask<int> FinishPending(IConnection connection, EntityId metadata, LoadoutId[] loadouts, ILogger logger)
    {
        var current = GameInstallMetadata.Load(connection.Db, metadata);
        if (!current.IsValid() || !current.Contains(GameInstallMetadata.RespellPending)) return 0;

        var finished = 0;
        foreach (var loadout in loadouts)
        {
            var sources = Read(connection.Db, loadout.Value, metadata, includeDisk: false);
            var marked = new List<Change>();
            Collect(marked, sources.Targets, LoadoutItemWithTargetPath.TargetPath, null, onlyMarked: true);
            Collect(marked, sources.Pins, LoadoutFileWinner.TargetPath, null, onlyMarked: true);
            Collect(marked, sources.Installed, GameInstalledFile.Path, null, onlyMarked: true);
            Collect(marked, sources.Claims, GameInstalledClaim.Path, null, onlyMarked: true);
            if (marked.Count > 0) await CommitSecondPhase(connection, marked);
            finished += marked.Count;
        }

        using var tx = connection.BeginTransaction();
        tx.RetractCurrent(metadata, GameInstallMetadata.RespellPending);
        await tx.Commit();
        if (finished > 0) logger.LogInformation("Finished rewriting {Count} recorded paths a previous run left half done", finished);
        return finished;
    }

    /// <summary>
    /// Rewrites every recorded path of the game whose spelling differs from <paramref name="spellings"/>. Reads the database
    /// as it is now, not through a snapshot the caller took: the reindex that precedes an apply records rows after it.
    /// Returns the number of rows rewritten.
    /// </summary>
    public static async ValueTask<int> Respell(IConnection connection, LoadoutId loadout, EntityId metadata, FolderSpellings spellings, IGamePathFilter filter, int chunk = 10_000)
    {
        var sources = Read(connection.Db, loadout.Value, metadata, includeDisk: true);
        var changes = new List<Change>();
        Collect(changes, sources.Targets, LoadoutItemWithTargetPath.TargetPath, spellings, filter: filter);
        Collect(changes, sources.Pins, LoadoutFileWinner.TargetPath, spellings, filter: filter);
        Collect(changes, sources.Installed, GameInstalledFile.Path, spellings, filter: filter);
        Collect(changes, sources.Claims, GameInstalledClaim.Path, spellings, filter: filter);
        var disk = sources.Disk
            .Where(row => !filter.ShouldFilter(row.GamePath))
            .Select(row => (Row: row, Canonical: spellings.Canonical(row.GamePath).Path.ToString()))
            .Where(c => !c.Canonical.Equals(c.Row.Path, StringComparison.Ordinal))
            .ToList();
        var total = changes.Count + disk.Count;
        if (total == 0) return 0;

        await SetPending(connection, metadata, true);
        var all = changes.Select(c => (Referenced: (Change?)c, Disk: ((PathRow Row, string Canonical)?)null))
            .Concat(disk.Select(d => (Referenced: (Change?)null, Disk: ((PathRow Row, string Canonical)?)d)))
            .ToList();
        for (var offset = 0; offset < all.Count; offset += chunk)
        {
            var slice = all.Skip(offset).Take(chunk).ToList();
            var referenced = slice.Select(s => s.Referenced).OfType<Change>().ToList();
            var recreated = slice.Select(s => s.Disk).OfType<(PathRow Row, string Canonical)>().ToList();
            await CommitFirstPhase(connection, referenced, recreated, metadata);
            if (referenced.Count > 0) await CommitSecondPhase(connection, referenced);
        }
        await SetPending(connection, metadata, false);
        return total;
    }

    private static Sources Read(IDb db, EntityId loadout, EntityId metadata, bool includeDisk) => new(
        GamePathRows.TargetPaths(db, loadout),
        GamePathRows.PinPaths(db, loadout),
        GamePathRows.GameInstalledFilePaths(db, loadout),
        GamePathRows.GameInstalledClaimPaths(db, loadout),
        includeDisk ? GamePathRows.DiskStatePaths(db, metadata) : []);

    private static void Collect(List<Change> changes, List<PathRow> rows, GamePathParentAttribute attribute, FolderSpellings? spellings, IGamePathFilter? filter = null, bool onlyMarked = false)
    {
        foreach (var row in rows)
        {
            var marked = row.Path.EndsWith(Marker, StringComparison.Ordinal);
            if (onlyMarked && !marked) continue;

            var path = marked ? row.Path[..^Marker.Length] : row.Path;
            var text = path;
            if (spellings is not null)
            {
                var asPath = new GamePath(row.Location, RelativePath.FromUnsanitizedInput(path));
                if (filter?.ShouldFilter(asPath) == true) continue;
                text = spellings.Canonical(asPath).Path.ToString();
            }

            if (!marked && text.Equals(row.Path, StringComparison.Ordinal)) continue;
            changes.Add(new Change(row, attribute, text));
        }
    }

    private static async ValueTask CommitFirstPhase(IConnection connection, List<Change> referenced, List<(PathRow Row, string Canonical)> recreated, EntityId metadata)
    {
        if (referenced.Count == 0 && recreated.Count == 0) return;
        var db = connection.Db;
        using var tx = connection.BeginTransaction();
        foreach (var change in referenced)
        {
            if (change.Row.Path.EndsWith(Marker, StringComparison.Ordinal)) continue;
            tx.Retract(change.Row.Row, change.Attribute, change.Row.Tuple);
            tx.Add(change.Row.Row, change.Attribute, (change.Row.Parent, change.Row.Location, RelativePath.FromUnsanitizedInput(change.Canonical + Marker)));
        }

        foreach (var (row, canonical) in recreated)
        {
            var old = DiskStateEntry.Load(db, row.Row);
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = (row.Parent, row.Location, RelativePath.FromUnsanitizedInput(canonical)),
                Identity = old.IdentityOf(),
                XxHash3 = old.IdentityOf().TryXxHash3(out var hash) ? hash : null,
                Size = old.Size,
                LastModified = old.LastModified,
                GameId = metadata,
            };
            tx.Delete(row.Row, recursive: false);
        }
        await tx.Commit();
    }

    private static async ValueTask CommitSecondPhase(IConnection connection, List<Change> referenced)
    {
        using var tx = connection.BeginTransaction();
        foreach (var change in referenced)
        {
            var marked = change.Row.Path.EndsWith(Marker, StringComparison.Ordinal) ? change.Row.Path : change.Canonical + Marker;
            tx.Retract(change.Row.Row, change.Attribute, (change.Row.Parent, change.Row.Location, RelativePath.FromUnsanitizedInput(marked)));
            tx.Add(change.Row.Row, change.Attribute, (change.Row.Parent, change.Row.Location, RelativePath.FromUnsanitizedInput(change.Canonical)));
        }
        await tx.Commit();
    }

    private static async ValueTask SetPending(IConnection connection, EntityId metadata, bool pending)
    {
        using var tx = connection.BeginTransaction();
        if (pending) tx.Add(metadata, GameInstallMetadata.RespellPending, true);
        else tx.RetractCurrent(metadata, GameInstallMetadata.RespellPending);
        await tx.Commit();
    }
}
