using Microsoft.Extensions.FileSystemGlobbing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

/// <summary>What <see cref="ToolOutputCapture.CaptureFolder"/> made of a folder.</summary>
/// <param name="Group">The group that holds the files, new or reused.</param>
/// <param name="FileCount">How many files it holds now.</param>
public readonly record struct ToolOutputCaptureResult(LoadoutItemGroupId Group, int FileCount);

/// <summary>
/// Turns what a tool wrote into a group of the loadout. A tool's output exists to replace files the mods ship
/// (LOD meshes, cleaned plugins), so a group made here ranks above every mod installed before it. A group
/// without a priority row ranks zero and would lose to all of them.
/// </summary>
public class ToolOutputCapture(IConnection connection, IFileStore fileStore)
{
    /// <summary>
    /// Makes <paramref name="folder"/>'s files the items of the top-level group called <paramref name="groupName"/>,
    /// placed under <paramref name="mapsTo"/>. A group of that name from an earlier run keeps its place in the
    /// order and has its items replaced: a tool that regenerates everything must not leave last run's files behind.
    /// </summary>
    /// <param name="ignored">
    /// Globs matched against each file's path relative to the folder, ignoring case (<c>**/*.log</c> for logs at
    /// any depth). Matching files stay out of the loadout.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Nothing is left to keep. A run that wrote only logs has failed, and replacing the previous output with an
    /// empty group would delete it from the game folder at the next sync.
    /// </exception>
    public async Task<ToolOutputCaptureResult> CaptureFolder(
        Loadout.ReadOnly loadout,
        AbsolutePath folder,
        GamePath mapsTo,
        string groupName,
        IReadOnlyList<string> ignored,
        CancellationToken cancellationToken = default)
    {
        var kept = ReadFolder(folder, ignored);
        if (kept.Count == 0)
            throw new InvalidOperationException($"Nothing in {folder} is left to keep for '{groupName}'; the previous output was left as it was.");

        var identities = new List<(RelativePath Path, FileIdentity Identity, Size Size, AbsolutePath Source)>(kept.Count);
        foreach (var (relative, source) in kept)
        {
            await using var stream = source.Read();
            var hash = await Xx3Hasher.HashAsync(stream, cancellationToken: cancellationToken);
            identities.Add((relative, FileIdentity.XxHash3(hash), source.FileInfo.Size, source));
        }

        var missing = new List<ArchivedFileEntry>();
        foreach (var file in identities.DistinctBy(f => f.Identity))
        {
            if (await fileStore.HaveFile(file.Identity) is false)
                missing.Add(new ArchivedFileEntry(new NativeFileStreamFactory(file.Source), file.Identity, file.Size));
        }

        await fileStore.BackupFiles(missing, deduplicate: false, cancellationToken);

        // Read after the (slow) hashing and backup, so a run that finished meanwhile elsewhere is not overwritten blind.
        var db = connection.Db;
        var loadoutId = loadout.LoadoutId;
        using var tx = connection.BeginTransaction();
        var existing = FindGroup(db, loadoutId, groupName);
        LoadoutItemGroupId group;
        if (existing is { } held)
        {
            group = LoadoutItemGroupId.From(held.Id);
            foreach (var child in held.Children) tx.Delete(child.Id, recursive: false);
            if (held.AsLoadoutItem().IsDisabled) tx.Retract(held.Id, LoadoutItem.Disabled, Null.Instance);
        }
        else
        {
            group = LoadoutManager.AddEmptyGroup(tx, loadoutId, groupName);
        }

        foreach (var file in identities)
            AddFile(tx, loadoutId, group, new GamePath(mapsTo.LocationId, mapsTo.Path / file.Path), file.Identity, file.Size);

        var result = await tx.Commit();
        return new ToolOutputCaptureResult(result[group], identities.Count);
    }

    /// <summary>
    /// Switches the group called <paramref name="groupName"/> off or on. A group that does not exist, or is already
    /// in the wanted state, is left alone. Returns whether anything changed.
    /// </summary>
    public async Task<bool> SetEnabled(LoadoutId loadoutId, string groupName, bool enabled)
    {
        if (FindGroup(connection.Db, loadoutId, groupName) is not { } group) return false;

        var item = group.AsLoadoutItem();
        if (item.IsDisabled == (enabled is false)) return false;

        using var tx = connection.BeginTransaction();
        if (enabled) tx.Retract(group.Id, LoadoutItem.Disabled, Null.Instance);
        else tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
        return true;
    }

    private static LoadoutItemGroup.ReadOnly? FindGroup(IDb db, LoadoutId loadoutId, string groupName)
    {
        foreach (var group in LoadoutItem.FindByLoadout(db, loadoutId).OfTypeLoadoutItemGroup())
        {
            var item = group.AsLoadoutItem();
            if (item.HasParent() || item.Name != groupName) continue;
            if (group.TryGetAsLoadoutOverridesGroup(out _)) continue;
            return group;
        }

        return null;
    }

    private static List<(RelativePath Relative, AbsolutePath Source)> ReadFolder(AbsolutePath folder, IReadOnlyList<string> ignored)
    {
        var matcher = new Matcher();
        matcher.AddInclude("**/*");
        foreach (var glob in ignored) matcher.AddExclude(glob);

        var files = new List<(RelativePath, AbsolutePath)>();
        foreach (var file in folder.EnumerateFiles())
        {
            var relative = file.RelativeTo(folder);
            if (matcher.Match(relative.ToString()).HasMatches) files.Add((relative, file));
        }

        return files;
    }

    private static void AddFile(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId group, GamePath path, FileIdentity identity, Size size)
    {
        _ = new LoadoutFile.New(tx, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = loadoutId,
                    ParentId = group,
                    Name = path.FileName,
                },
                TargetPath = path.ToGamePathParentTuple(loadoutId),
            },
            Hash = identity.XxHash3(),
            Size = size,
        };
    }
}
