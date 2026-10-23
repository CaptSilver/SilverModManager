using System.Text.Json.Serialization;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Overlay;

/// <summary>
/// What the small files of the game folder looked like at one moment. A modded session must leave the real folder
/// as it found it, so comparing two snapshots is the backstop for an overlay that never reached the game: its
/// writes would land in the folder and the upper folder would look clean.
/// </summary>
public sealed record GameFolderSnapshot(IReadOnlyList<GameFolderSnapshot.Entry> Entries)
{
    /// <param name="Path">The path relative to the game folder, with forward slashes.</param>
    /// <param name="Size">The size in bytes; for a link, the length of its target text.</param>
    /// <param name="ModifiedTicks">The UTC modification time in ticks.</param>
    /// <param name="Hash">The xxHash3 of the content as hex, or the link's target for a link.</param>
    public sealed record Entry(string Path, long Size, long ModifiedTicks, string Hash);

    /// <summary>How a path differs between two snapshots.</summary>
    public sealed record Difference(string Path, string Change);

    /// <summary>
    /// Records every file of the game folder except the archives and videos, which add up to tens of gigabytes and are
    /// never read (the same filter the synchronizer indexes with, for the game's <paramref name="layout"/>). A link is
    /// recorded as its target, not followed.
    /// </summary>
    public static async Task<GameFolderSnapshot> Take(AbsolutePath gameFolder, FromSoftGameLayout layout, CancellationToken cancellationToken = default)
    {
        var entries = new List<Entry>();
        if (!gameFolder.DirectoryExists()) return new GameFolderSnapshot(entries);

        var root = gameFolder.GetFullPath();
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (FromSoftPaths.IsArchiveOrMedia(layout, new GamePath(LocationId.Game, $"{layout.GameSubfolder}/{relative}"))) continue;

            var info = new FileInfo(file);
            if (info.LinkTarget is { } target)
            {
                entries.Add(new Entry(relative, target.Length, info.LastWriteTimeUtc.Ticks, $"link:{target}"));
                continue;
            }

            var hash = await gameFolder.FileSystem.FromUnsanitizedFullPath(file).XxHash3Async(token: cancellationToken);
            entries.Add(new Entry(relative, info.Length, info.LastWriteTimeUtc.Ticks, hash.Value.ToString("x16")));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new GameFolderSnapshot(entries);
    }

    /// <summary>Every path added, removed or changed between this snapshot and <paramref name="now"/>, in path order.</summary>
    public IReadOnlyList<Difference> Compare(GameFolderSnapshot now)
    {
        var before = Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var after = now.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);

        var differences = new List<Difference>();
        foreach (var (path, entry) in before)
        {
            if (!after.TryGetValue(path, out var current)) differences.Add(new Difference(path, "removed"));
            else if (entry != current) differences.Add(new Difference(path, "changed"));
        }

        foreach (var path in after.Keys.Where(p => !before.ContainsKey(p))) differences.Add(new Difference(path, "added"));

        differences.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return differences;
    }
}
