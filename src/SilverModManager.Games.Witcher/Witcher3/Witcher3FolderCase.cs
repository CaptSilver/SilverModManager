using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3;

/// <summary>
/// The game folder's <c>Mods</c>, <c>dlc</c> and <c>bin</c> folders have one spelling. Wine resolves names
/// case-insensitively, so <c>mods</c> next to <c>Mods</c> leaves the game with only one of them.
/// </summary>
internal static class Witcher3FolderCase
{
    internal static readonly string[] TopFolders = [WitcherConstants.W3ModsFolder, WitcherConstants.W3DlcFolder, "bin"];

    /// <summary>
    /// <paramref name="path"/> with its first segment written in the game's spelling when it case-insensitively
    /// names one of the game's top folders; the same text otherwise.
    /// </summary>
    public static string Canonical(string path)
    {
        var slash = path.IndexOf('/');
        var first = slash < 0 ? path : path[..slash];
        foreach (var folder in TopFolders)
            if (first.Equals(folder, StringComparison.OrdinalIgnoreCase))
                return folder + path[first.Length..];
        return path;
    }

    // The attribute holding a path compares text without regard to case, so one transaction that swaps a path
    // for its re-cased twin cancels itself out. The row passes through a different spelling instead, and a path
    // found still carrying the marker (the process died between the two commits) is finished first. Each commit is a
    // loadout revision, so anything watching the loadout can see paths ending in the marker between the two.
    private const string Marker = "~recase";

    private readonly record struct Change(EntityId Row, GamePathParentAttribute Attribute, (EntityId Parent, LocationId Location, RelativePath Path) Old, string Canonical);

    /// <summary>
    /// Rewrites the loadout's items and the game's recorded disk state whose game-folder path starts with a case
    /// variant of a top folder, left by installs from before the plan wrote them canonically. The disk state is
    /// rewritten too because the synchronizer takes the spelling it deletes by from it. Returns the loadout
    /// unchanged when nothing needed rewriting. Reads the database as it is now, not through the caller's snapshot:
    /// the reindex that opens an apply records disk state after that snapshot was taken.
    /// </summary>
    public static async ValueTask<Loadout.ReadOnly> Repair(IConnection connection, Loadout.ReadOnly loadout)
    {
        loadout = Loadout.Load(connection.Db, loadout.LoadoutId);
        var changes = new List<Change>();
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath())
            Collect(changes, item.Id, LoadoutItemWithTargetPath.TargetPath, item.TargetPath);
        foreach (var entry in DiskStateEntry.FindByGame(loadout.Db, loadout.Installation.Id))
            Collect(changes, entry.Id, DiskStateEntry.Path, entry.Path);
        if (changes.Count == 0) return loadout;

        await Commit(connection, changes);
        return loadout.Rebase();
    }

    private static void Collect(List<Change> changes, EntityId row, GamePathParentAttribute attribute, (EntityId Parent, LocationId Location, RelativePath Path) value)
    {
        if (value.Location != LocationId.Game) return;

        var current = value.Path.ToString();
        var canonical = Canonical(current.EndsWith(Marker, StringComparison.Ordinal) ? current[..^Marker.Length] : current);
        if (!canonical.Equals(current, StringComparison.Ordinal)) changes.Add(new Change(row, attribute, value, canonical));
    }

    private static async Task Commit(IConnection connection, List<Change> changes)
    {
        // First commit: the old spelling becomes the marked one. Second: the marked one becomes canonical.
        foreach (var second in new[] { false, true })
        {
            using var tx = connection.BeginTransaction();
            foreach (var change in changes)
            {
                var marked = change.Old.Path.ToString().EndsWith(Marker, StringComparison.Ordinal);
                if (!second && marked) continue;

                var from = second && !marked ? change.Canonical + Marker : change.Old.Path.ToString();
                var to = second ? change.Canonical : change.Canonical + Marker;
                tx.Retract(change.Row, change.Attribute, (change.Old.Parent, LocationId.Game, RelativePath.FromUnsanitizedInput(from)));
                tx.Add(change.Row, change.Attribute, (change.Old.Parent, LocationId.Game, RelativePath.FromUnsanitizedInput(to)));
            }
            await tx.Commit();
        }
    }

    /// <summary>
    /// Merges a folder that differs from the game's spelling of <c>Mods</c>, <c>dlc</c> or <c>bin</c> only in case into
    /// the canonical one, on a file system where the two are different folders (the Linux side of a Wine prefix).
    /// The loadout's rows compare paths without regard to case, so they cannot move these files: an apply would see
    /// the file already in place. A file whose twin already exists in the canonical folder stays where it is.
    /// </summary>
    public static void MergeFoldersOnDisk(AbsolutePath gameFolder, ILogger logger)
    {
        if (OperatingSystem.IsWindows() || !gameFolder.DirectoryExists()) return;

        foreach (var folder in TopFolders)
        {
            foreach (var variant in gameFolder.EnumerateDirectories(recursive: false).Where(d => !d.FileName.Equals(folder, StringComparison.Ordinal) && d.FileName.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                logger.LogInformation("Moving the contents of '{Variant}' into '{Folder}'", variant, folder);
                Merge(variant.GetFullPath(), gameFolder.Combine(folder).GetFullPath(), logger);
            }
        }
    }

    private static void Merge(string from, string to, ILogger logger)
    {
        if (!Directory.Exists(to))
        {
            Directory.Move(from, to);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(from).ToArray())
        {
            var destination = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Merge(entry, destination, logger);
            else if (!File.Exists(destination) && !Directory.Exists(destination)) File.Move(entry, destination);
            else logger.LogWarning("'{File}' stays because '{Destination}' exists", entry, destination);
        }

        if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
    }
}
