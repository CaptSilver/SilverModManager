using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Gives the mod groups of lists installed before <see cref="WabbajackModGroup.MainArchive"/> existed their archive and
/// library link. Only adds <see cref="WabbajackModGroup.MainArchive"/> and, for groups that predate it,
/// <see cref="WabbajackModGroup.Folder"/>, and adds or retracts <see cref="LibraryLinkedLoadoutItem.LibraryItem"/> on mod groups;
/// it never deletes or moves a file or a row.
/// </summary>
public sealed class ModGroupLinkBackfill(IConnection connection, IFileStore fileStore, ILibraryArchiveLocator locator, ModlistInstallRuns runs, ILogger<ModGroupLinkBackfill> logger)
{
    /// <summary>
    /// For every <see cref="WabbajackModlistLoadoutGroup"/>, in its own try/catch: when no <see cref="WabbajackModGroup"/>
    /// child carries <see cref="WabbajackModGroup.MainArchive"/>, classifies the list once and records it; then relinks.
    /// Skipped while an install runs into the group's loadout or the group is mid-update: that install's own link step
    /// does the work. The gate is checked before the list is read and again right before the commit, together with
    /// "still no child carries a main archive"; if either fails the transaction is dropped unwritten.
    /// </summary>
    /// <param name="afterClassify">Runs after the list is classified and before the second check; tests use it to change the group in that window.</param>
    public async Task Run(CancellationToken ct, Func<Task>? afterClassify = null)
    {
        foreach (var id in WabbajackModlistLoadoutGroup.All(connection.Db).Select(group => group.Id).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Link(id, ct, afterClassify);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not link the mod groups of modlist install {Group}", id);
            }
        }
    }

    private async Task Link(EntityId id, CancellationToken ct, Func<Task>? afterClassify)
    {
        var db = connection.Db;
        var group = WabbajackModlistLoadoutGroup.Load(db, id);
        if (!group.IsValid()) return;

        var loadoutId = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        if (!Loadout.Load(db, loadoutId).IsValid())
        {
            logger.LogWarning("The loadout of modlist install {Group} no longer exists; its mod groups are not linked", id);
            return;
        }
        if (IsBusy(group, loadoutId)) return;

        // Groups made before they recorded a folder are not valid mod groups until they have one, so nothing below can see them.
        if (await RecordFolders(id, ct))
        {
            db = connection.Db;
            group = WabbajackModlistLoadoutGroup.Load(db, id);
            if (!group.IsValid() || IsBusy(group, loadoutId)) return;
        }

        var mods = ModGroups(db, id).ToArray();
        if (mods.Length == 0)
        {
            logger.LogDebug("Modlist install {Group} has no mod groups to link", id);
            return;
        }

        IReadOnlyDictionary<string, FileIdentity>? recorded = null;
        if (!mods.Any(HasMainArchive))
        {
            var modlist = WabbajackModlistLibraryFile.Load(db, group.ModlistId.Value);
            var classified = await InstallModlistJob.ClassifyList(loadoutId, modlist, connection, fileStore, ct);
            recorded = ModGroupArchives.MainArchiveByFolder(classified.Placements, classified.BsaInputs, classified.List.Archives);
            if (afterClassify is not null) await afterClassify();
        }

        // The state the transaction is built from is read again here: an install or update may have started, or written
        // the archives itself, while the list was being read.
        db = connection.Db;
        group = WabbajackModlistLoadoutGroup.Load(db, id);
        if (!group.IsValid() || IsBusy(group, loadoutId)) return;
        if (recorded is not null && ModGroups(db, id).Any(HasMainArchive)) return;

        using var tx = connection.BeginTransaction();
        if (recorded is not null)
        {
            var folders = InstallModlistJob.LoadExisting(db, LoadoutItemGroupId.From(id)).Groups
                .Where(pair => pair.Key != Mo2Layout.ProfileSettingsGroup)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            ModGroupArchives.Record(tx, db, folders, recorded);
        }
        ModGroupArchives.Relink(tx, db, locator, id, recorded);
        if (!tx.IsEmpty) await tx.Commit();
    }

    /// <summary>
    /// Gives each mod group that has no <see cref="WabbajackModGroup.Folder"/> the one an install would record: its name.
    /// Returns whether it wrote any. This is the only thing besides the archive and the link the pass adds.
    /// </summary>
    private async Task<bool> RecordFolders(EntityId modlistGroup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = connection.Db;
        var state = InstallModlistJob.LoadExisting(db, LoadoutItemGroupId.From(modlistGroup));
        if (state.FolderlessGroups.Count == 0) return false;

        using var tx = connection.BeginTransaction();
        InstallModlistJob.AddFolders(tx, state);
        await tx.Commit();
        return true;
    }

    private bool IsBusy(WabbajackModlistLoadoutGroup.ReadOnly group, LoadoutId loadoutId)
        => runs.FindInto(loadoutId) is not null || ModlistInstallRuns.IsMidUpdate(group);

    private static IEnumerable<WabbajackModGroup.ReadOnly> ModGroups(IDb db, EntityId modlistGroup)
        => LoadoutItem.FindByParent(db, modlistGroup).OfTypeLoadoutItemGroup()
            .Select(child => WabbajackModGroup.Load(db, child.Id))
            .Where(mod => mod.IsValid());

    private static bool HasMainArchive(WabbajackModGroup.ReadOnly mod) => WabbajackModGroup.MainArchive.TryGetValue(mod, out _);
}

/// <summary>Runs <see cref="ModGroupLinkBackfill"/> once on the thread pool when the app starts; a failure is logged, not thrown.</summary>
public sealed class ModGroupLinkBackfillRunner(ModGroupLinkBackfill backfill, ILogger<ModGroupLinkBackfillRunner> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _closing = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // On the thread pool: reading each list must not delay the hosted services after this one.
        _ = Task.Run(RunSafely, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunSafely()
    {
        try
        {
            await backfill.Run(_closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Linking the mod groups of installed modlists failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _closing.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _closing.Dispose();
}
