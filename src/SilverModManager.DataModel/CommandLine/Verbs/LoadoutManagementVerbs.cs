using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Library;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// Loadout management verbs for the commandline interface
/// </summary>
public static class LoadoutManagementVerbs
{
    private const string ConfirmRemovalHelp = "Go ahead when the apply removes most of the mod files the loadout deployed, for example because a collection is switched off. Without it the command refuses and says why.";
    private const string LargeFilesHelp = "What to do when the added files exceed the backup cap: adopt or backup. Without it the command refuses and says so.";

    /// <summary>
    /// Register the loadout management verbs
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddLoadoutManagementVerbs(this IServiceCollection services) =>
        services
            .AddModule("loadouts", "Commands for managing loadouts as a whole")
            .AddModule("loadout", "Commands for managing a specific loadout")
            .AddModule("loadout groups", "Commands for managing the file groups in a loadout")
            .AddModule("loadout group", "Commands for managing a specific group of files in a loadout")
            .AddModule("loadout files", "Commands for the files a loadout provides")
            .AddModule("loadout group items", "Commands for managing the items in a group of files in a loadout")
            .AddModule("loadout group priority", "Commands for a group's place in the mod priority order")
            .AddModule("loadout priority", "Commands for the mod priority order of a loadout")
            .AddModule("loadout plugins", "Commands for the plugin load order of a loadout")
            .AddModule("loadout version", "Commands for managing the version of a loadout")
            .AddVerb(() => SetVersion)
            .AddVerb(() => Synchronize)
            .AddVerb(() => Create)
            .AddVerb(() => InstallMod)
            .AddVerb(() => Reindex)
            .AddVerb(() => ListLoadouts)
            .AddVerb(() => BackupFiles)
            .AddVerb(() => ListGroupContents)
            .AddVerb(() => ListGroups)
            .AddVerb(() => DeleteGroupItems)
            .AddVerb(() => RemoveLoadoutFiles)
            .AddVerb(() => ListUnwritableFiles)
            .AddVerb(() => KeepDiskFiles)
            .AddVerb(() => ForgetUnwritableFiles)
            .AddVerb(() => ListRevisions)
            .AddVerb(() => Revert)
            .AddVerb(() => EnableGroup)
            .AddVerb(() => DisableGroup)
            .AddLoadoutOrderVerbs();

    [Verb("loadout version set", "Sets the game version for a loadout")]
    private static async Task<int> SetVersion([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to set the version for")] Loadout.ReadOnly loadout,
        [Option("v", "version", "Version to set")] string version,
        [Injected] IFileHashesService hasherService)
    {
        if (!hasherService.TryGetLocatorIdsForVanityVersion(loadout.Installation.Store, VanityVersion.From(version), out var newCommonIds))
        {
            await renderer.Error("Version {0} not found", version);
            return -1;
        }

        // Get the actual version from the ids, so that we can sanitize the version string, and collapse multiple
        // versions into a single version string
        if (!hasherService.TryGetVanityVersion((loadout.Installation.Store, newCommonIds), out var actualVersion))
        {
            await renderer.Error("Version {0} not found", version);
            return -1;
        }
        
        using var tx = loadout.Db.Connection.BeginTransaction();

        loadout = loadout.Rebase();
        // Retract the old ids
        foreach (var existingId in loadout.LocatorIds)
            tx.Retract(loadout, Loadout.LocatorIds, existingId);
        // And the old version
        tx.Retract(loadout, Loadout.GameVersion, loadout.GameVersion);
        
        // Add the new ids and version
        foreach (var newId in newCommonIds)
            tx.Add(loadout, Loadout.LocatorIds, newId);
        
        tx.Add(loadout, Loadout.GameVersion, actualVersion);
        
        await tx.Commit();
        
        return 0;
    }

    [Verb("loadout synchronize", "Synchronize the loadout with the game folders, adding any changes in the game folder to the loadout and applying any new changes in the loadout to the game folder")]
    private static async Task<int> Synchronize([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to apply")] Loadout.ReadOnly loadout,
        [Injected] ISynchronizerService syncService,
        [Option("large-files", "large-files", LargeFilesHelp, true)] string? largeFiles,
        [Option("confirm-removal", "confirm-removal", ConfirmRemovalHelp, true)] bool confirmRemoval)
    {
        using var origin = SyncOrigin.Begin(SyncTrigger.CommandLine);
        using var removal = confirmRemoval ? MassRemovalConsent.Confirmed() : MassRemovalConsent.Refused();
        await syncService.Synchronize(loadout.LoadoutId, BackupOverflowChoices.Parse(largeFiles));
        foreach (var file in syncService.GetUnwritableFiles(loadout.LoadoutId))
            await renderer.Text("Not written: {0}. See 'loadout files unwritable'.", file.Describe());
        return 0;
    }

    [Verb("loadout create", "Creates a loadout for a game found on this machine, managing it if it is not managed yet")]
    private static async Task<int> Create([Injected] IRenderer renderer,
        [Injected] IGameRegistry registry,
        [Injected] ILoadoutManager loadoutManager,
        [Option("g", "game", "The game id, as `games list --json` prints it")] string gameId,
        [Option("n", "name", "Loadout name", true)] string? name,
        [Option("large-files", "large-files", LargeFilesHelp, true)] string? largeFiles)
    {
        var choice = BackupOverflowChoices.Parse(largeFiles);
        var installations = GameVerbs.Located(registry, gameId);
        if (installations.IsEmpty)
        {
            await renderer.TextLine(GameVerbs.NotLocatedMessage(gameId));
            return 1;
        }

        // A game found in two stores gets the first one the registry lists; the output names it.
        var installation = installations[0];
        var loadout = await loadoutManager.CreateLoadout(installation, name, choice);
        await renderer.TextLine($"Created loadout {loadout.Name} ({loadout.LoadoutId}) for {installation.Game.DisplayName} at {installation.LocatorResult.Path}");
        return 0;
    }

    [Verb("loadout backup-game-files", "Archives all the files on disk that are otherwise not backed up")]
    private static async Task<int> BackupFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to backup files for")]
        Loadout.ReadOnly loadout,
        [Injected] IConnection connection,
        [Injected] IFileStore fileStore)
    {
        var game = loadout.InstallationInstance.GetGame();
        var gameInstallation = loadout.InstallationInstance;
        var tree = await game.Synchronizer.BuildSyncTree(loadout);
        game.Synchronizer.ProcessSyncTree(tree);
        var wanted = tree.Where(f => f.Value.HaveDisk &&
                                     !f.Value.Signature.HasFlag(Signature.DiskArchived) &&
                                     f.Value.SourceItemType == LoadoutSourceItemType.Game)
            .ToDictionary(f => f.Key, f => f.Value with { Actions = Actions.BackupFile });

        // A file the store vouched for is named by its SHA-1 until something reads it, and the file store is keyed by xxHash3.
        using var tx = connection.BeginTransaction();
        await game.Synchronizer.ResolveDiskIdentitiesForWrites(wanted, gameInstallation, tx);
        await tx.Commit();

        var toBackup = wanted
            .Select(f => (Path: gameInstallation.Locations.ToAbsolutePath(f.Key), f.Value.Disk.Size, Identity: f.Value.Disk.Identity))
            .ToArray();
        await renderer.TextLine("Backing up {0} files for a total of {1}", toBackup.Length, toBackup.Aggregate(Size.Zero, (a, b) => a + b.Size));
        
        var entries = toBackup.Select(f => new ArchivedFileEntry(new NativeFileStreamFactory(f.Path), f.Identity, f.Size)).ToArray();
        await fileStore.BackupFiles(entries);
        return 0;
    }

    
    [Verb("loadout install", "Installs a mod into a loadout")]
    private static async Task<int> InstallMod([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Option("f", "file", "Mod file to install")] AbsolutePath file,
        [Option("n", "name", "Name of the mod after installing")] string name,
        [Injected] ILibraryService libraryService,
        [Injected] ILoadoutManager loadoutManager,
        [Injected] CancellationToken token)
    {
        return await renderer.WithProgress(token, async () =>
        {
            var localFile = await libraryService.AddLocalFile(file, new LoadoutAcquisition(loadout.LoadoutId));
            await loadoutManager.InstallItem(localFile.AsLibraryFile().AsLibraryItem(), loadout);
            return 0;
        });
    }
    
    [Verb("loadout reindex", "Re-indexes the on-disk state of the loadout")]
    private static async Task<int> Reindex([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Injected] CancellationToken token)
    {
        await renderer.Text("Reindexing {0}", loadout.Name);
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        await synchronizer.ReindexState(loadout.InstallationInstance, cancellationToken: token);
        return 0;
    }

    private sealed record LoadoutRow(string Id, string Name, string Game, string Version, string Store, int Items);

    [Verb("loadouts list", "Lists all the loadouts")]
    private static async Task<int> ListLoadouts([Injected] IRenderer renderer,
        [Injected] IConnection conn,
        [Option("j", "json", "Print the loadouts as JSON", true)] bool json,
        [Injected] CancellationToken token)
    {
        var db = conn.Db;
        var rows = Loadout.All(db)
            .Where(x => x.IsVisible())
            .Select(list => new LoadoutRow(list.LoadoutId.ToString(), list.Name, list.Installation.Name, list.GameVersion.ToString(), list.Installation.Store.ToString(), LoadoutItem.FindByLoadout(db, list.Id).Count))
            .ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        await rows
            .Select(row => (row.Id, row.Name, row.Game, row.Version, row.Store, row.Items))
            .RenderTable(renderer, "Id", "Name", "Game", "Version", "Store", "Items");
        return 0;
    }

    private sealed record GroupFileRow(string Folder, string File, string? Hash);

    [Verb("loadout group list", "Lists the contents of a loadout group")]
    private static async Task<int> ListGroupContents([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("g", "group", "Name of the group to list")] string groupName,
        [Option("f", "filterFiles", "Filter files by the given glob", true)] Matcher? filterFiles,
        [Option("j", "json", "Print the files as JSON", true)] bool json,
        [Injected] CancellationToken token)
    {
        var found = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .FirstOrOptional(m => m.AsLoadoutItem().Name == groupName);

        if (!found.HasValue)
            return await renderer.InputError("Group {0} not found", groupName);
        var mod = found.Value;

        Func<string, bool> filter = _ => true;

        if (filterFiles != null) 
            filter = s => filterFiles.Match(s).HasMatches;
        
        var files = mod.Children
            .Select(c =>
                {
                    var hasPath = c.TryGetAsLoadoutItemWithTargetPath(out var withPath);
                    var hasFile = withPath.TryGetAsLoadoutFile(out var withFile);

                    if (hasPath && hasFile)
                        return (withPath.TargetPath.Item2, withPath.TargetPath.Item3, withFile.Hash.ToString());

                    if (hasPath)
                        return (withPath.TargetPath.Item2, withPath.TargetPath.Item3, "<none>");

                    return default((LocationId, RelativePath, string));

                })
            .Where(v => v != default((LocationId, RelativePath, string)))
            .Where(f => filter(f.Item2.ToString()))
            .OrderBy(v => v.Item1)
            .ThenBy(v => v.Item2)
            .ToArray();

        if (json)
        {
            var rows = files.Select(f => new GroupFileRow(f.Item1.ToString(), f.Item2.ToString(), f.Item3 == "<none>" ? null : f.Item3));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        await files.RenderTable(renderer, "Folder", "File", "Hash");
        return 0;
    }

    [Verb("loadout group items delete", "Deletes items from a group that match a given pattern")]
    private static async Task<int> DeleteGroupItems(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("g", "group", "Name of the group to list")] string groupName,
        [Option("f", "filterFiles", "Filter files by the given glob")] Matcher filterFiles,
        [Injected] ILoadoutFileRemover remover,
        [Injected] CancellationToken token)
    {
        var mod = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .FirstOrOptional(m => m.AsLoadoutItem().Name == groupName);

        if (!mod.HasValue)
            return await renderer.InputError("Group {0} not found", groupName);

        var paths = mod.Value.Children
            .OfTypeLoadoutItemWithTargetPath()
            .Where(t => filterFiles.Match(t.TargetPath.Item3.ToString()).HasMatches)
            .Select(t => (GamePath)t.TargetPath)
            .Distinct()
            .ToArray();

        await renderer.Text("Deleting {0} items", paths.Length);
        if (paths.Length > 0)
        {
            var request = new FileRemovalRequest(loadout.LoadoutId, paths, [LoadoutItemGroupId.From(mod.Value.Id)]);
            await remover.Remove(request, includeLocked: true);
        }

        await renderer.Text("Complete");

        return 0;
    }

    [Verb("loadout files remove", "Removes files, or whole folders, from every mod in a loadout, as the Delete button of the Apply preview does. Files inside a Nexus collection or Wabbajack modlist are skipped unless --include-locked is given. The next apply deletes them from the game folder; a restore point is written first, see 'loadout revisions'.")]
    private static async Task<int> RemoveLoadoutFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to remove the files from")] Loadout.ReadOnly loadout,
        [Option("p", "paths", "Files or folders in the game folder, separated by ';', for example 'Data/Foo.dll;Data/SKSE/Plugins/Bar'")] string pathList,
        [Option("include-locked", "include-locked", "Also remove files inside a collection or modlist that decides what it contains", true)] bool includeLocked,
        [Injected] ILoadoutFileRemover remover,
        [Injected] CancellationToken token)
    {
        var paths = pathList
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UnwritableFile.ParsePath)
            .ToArray();
        var request = new FileRemovalRequest(loadout.LoadoutId, paths);

        if (!includeLocked)
        {
            foreach (var row in remover.Plan(request).Locked)
                await renderer.Text("Skipping {0}: {1}", row.Path.Path.ToString(), row.LockReason ?? "");
        }

        var result = await remover.Remove(request, includeLocked);
        await renderer.Text("Removed {0} files. Run 'loadout synchronize' to apply; 'loadout revisions' lists the restore point.", result.Removed.Count);
        return 0;
    }

    [Verb("loadout files unwritable", "Lists the files the loadout names that an apply cannot write, because no copy of them is kept: the Apply preview marks them 'No copy kept'. Settle them with 'loadout files accept-disk' or 'loadout files forget'.")]
    private static async Task<int> ListUnwritableFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to list the files of")] Loadout.ReadOnly loadout,
        [Injected] ISynchronizerService syncService)
    {
        var files = syncService.GetUnwritableFiles(loadout.LoadoutId);
        if (files.Count == 0)
        {
            await renderer.Text("No file is waiting on a copy that was never kept.");
            return 0;
        }

        foreach (var file in files) await renderer.Text("{0}", file.Describe());
        return 0;
    }

    [Verb("loadout files accept-disk", "Settles files an apply cannot write, for which the game folder has another version: the file on disk is backed up and becomes the loadout's version of the path, so the next apply has nothing to do for it. Files that are not in the game folder are skipped; see 'loadout files forget'.")]
    private static Task<int> KeepDiskFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to settle the files of")] Loadout.ReadOnly loadout,
        [Option("p", "paths", "Files separated by ';', as 'loadout files unwritable' lists them: a game folder path as is, another location as {Location}/path. Every file that fits when left out", true)] string? pathList,
        [Injected] ISynchronizerService syncService,
        [Injected] CancellationToken token)
        => ResolveUnwritable(renderer, syncService, loadout, pathList, UnwritableResolution.KeepDiskFile, token);

    [Verb("loadout files forget", "Settles files an apply cannot write that are not in the game folder: the loadout stops asking for them by recording a deletion. Files the game folder has a version of are skipped; see 'loadout files accept-disk'.")]
    private static Task<int> ForgetUnwritableFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to settle the files of")] Loadout.ReadOnly loadout,
        [Option("p", "paths", "Files separated by ';', as 'loadout files unwritable' lists them: a game folder path as is, another location as {Location}/path. Every file that fits when left out", true)] string? pathList,
        [Injected] ISynchronizerService syncService,
        [Injected] CancellationToken token)
        => ResolveUnwritable(renderer, syncService, loadout, pathList, UnwritableResolution.Forget, token);

    private static async Task<int> ResolveUnwritable(IRenderer renderer, ISynchronizerService syncService, Loadout.ReadOnly loadout, string? pathList, UnwritableResolution resolution, CancellationToken token)
    {
        var paths = pathList?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UnwritableFile.ParsePath)
            .ToArray();
        var result = await syncService.ResolveUnwritableFiles(loadout.LoadoutId, paths, resolution, token);
        foreach (var (path, why) in result.Skipped) await renderer.Text("Skipping {0}: {1}", UnwritableFile.Name(path), why);
        await renderer.Text("Settled {0} files. The next apply has nothing to do for them.", result.Resolved.Count);
        return 0;
    }

    private sealed record GroupRow(string Id, string Name, int Items, bool IsEnabled, string? Parent);

    [Verb("loadout groups list", "Lists the groups in a loadout")]
    private static async Task<int> ListGroups([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the groups as JSON, with the ids the -g options accept", true)] bool json,
        [Injected] CancellationToken token)
    {
        var groups = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .ToArray();

        if (json)
        {
            var rows = groups.Select(group =>
            {
                var item = group.AsLoadoutItem();
                return new GroupRow(group.Id.ToString(), item.Name, group.Children.Count, item.IsEnabled(), item.HasParent() ? item.Parent.AsLoadoutItem().Name : null);
            });
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        await groups
            .Select(mod => (mod.AsLoadoutItem().Name, mod.Children.Count))
            .RenderTable(renderer, "Name", "Items");

        return 0;
    }

    private sealed record RevisionRow(int Number, DateTimeOffset Timestamp, string Tx, int ModCount);

    [Verb("loadout revisions", "Lists revisions for a loadout")]
    private static async Task<int> ListRevisions([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the revisions as JSON; the number is what 'loadout revert -r' takes", true)] bool json,
        [Injected] IConnection connection,
        [Injected] UndoService undoService,
        [Injected] CancellationToken token)
    {
        var revisions = undoService.RevisionsFor(loadout)
            .OrderBy(r => r.Revision.Timestamp)
            .Select((r, idx) => new RevisionRow(idx, r.Revision.Timestamp, r.Revision.TxEntity.ToString(), r.ModCount))
            .ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(revisions, CliJson.Options));
            return 0;
        }

        await revisions
            .Select(r => (r.Number, r.Timestamp, r.Tx, r.ModCount))
            .RenderTable(renderer, "Rev #", "Timestamp", "Tx", "ModCount");

        return 0;
    }

    [Verb("loadout group enable", "Switches a group on. Groups a collection or the game's install controls are left alone and named. A restore point is written first, see 'loadout revisions'.")]
    private static Task<int> EnableGroup([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout the group is in")] Loadout.ReadOnly loadout,
        [Option("g", "group", "The group's name, or its id from 'loadout groups list --json' when two share a name")] string group,
        [Injected] ILoadoutManager loadoutManager) => SetGroupSwitch(renderer, loadout, group, enabled: true, loadoutManager);

    [Verb("loadout group disable", "Switches a group off; its files stay in the loadout. Groups a collection or the game's install controls are left alone and named. A restore point is written first, see 'loadout revisions'.")]
    private static Task<int> DisableGroup([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout the group is in")] Loadout.ReadOnly loadout,
        [Option("g", "group", "The group's name, or its id from 'loadout groups list --json' when two share a name")] string group,
        [Injected] ILoadoutManager loadoutManager) => SetGroupSwitch(renderer, loadout, group, enabled: false, loadoutManager);

    private static async Task<int> SetGroupSwitch(IRenderer renderer, Loadout.ReadOnly loadout, string groupName, bool enabled, ILoadoutManager loadoutManager)
    {
        var group = LoadoutGroupLookup.Find(loadout, groupName);
        var name = group.AsLoadoutItem().Name;
        var result = await loadoutManager.SetGroupsEnabled(loadout.LoadoutId, [LoadoutItemId.From(group.Id)], enabled);

        foreach (var (_, reason) in result.Skipped)
            await renderer.TextLine("{0}", $"{name} was left alone: {reason}.");

        if (result.Changed.Count > 0)
        {
            await renderer.TextLine("{0}", $"{name} is switched {(enabled ? "on" : "off")}. 'loadout revisions' lists the restore point; 'loadout revert' undoes it.");
            return 0;
        }

        if (result.Skipped.Count > 0) return 1;

        await renderer.TextLine("{0}", $"{name} is already switched {(enabled ? "on" : "off")}.");
        return 0;
    }

    [Verb("loadout revert", "Reverts a loadout to a specific revision")]
    private static async Task<int> Revert([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("r", "revisionNumber", "Revision number to revert to")] int revisionNumber,
        [Injected] IConnection connection,
        [Injected] UndoService undoService,
        [Injected] CancellationToken token)
    {
        var revisions = undoService.RevisionsFor(loadout);
        if (!RevisionSelection.TryGet(revisions, revisionNumber, out var revision))
        {
            await renderer.Error("Revision {0} not found, this loadout has revisions 0 to {1}", revisionNumber, revisions.Count - 1);
            return 1;
        }

        var result = await undoService.RevertTo(revision);

        foreach (var (entity, attribute, target) in result.DanglingReferences)
            await renderer.Text("Restored {0} refers to {1} through {2}, which no longer exists", entity, target, connection.AttributeCache.GetSymbol(attribute));

        foreach (var (entity, attribute) in result.Residual)
            await renderer.Error("{0} still differs from the revision on {1}", entity, connection.AttributeCache.GetSymbol(attribute));

        return result.Residual.Count == 0 ? 0 : 1;
    }
}
