using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Update;

/// <summary>
/// Moves an installed Wabbajack list to a newer version of it: the read-only review, the archive downloads, the apply
/// (the install job switching the group under a restore point) and the undo of it. Keeping or releasing the restore
/// point is <see cref="ModlistUpdateFinish"/>.
/// </summary>
public class ModlistUpdater(IServiceProvider provider)
{
    private IConnection Connection => provider.GetRequiredService<IConnection>();

    /// <summary>What the update would do, worked out without doing any of it.</summary>
    public virtual Task<ModlistUpdatePreviewResult> Review(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile, CancellationToken ct)
        => new ModlistUpdatePreview(provider).Preview(group, newFile, ct);

    /// <summary>
    /// Whether the library holds every archive <paramref name="newFile"/> needs, checked now with the same check
    /// <see cref="Apply"/> refuses on: the library may have gained archives, or complete copies of ones it held
    /// incompletely, since the review was made.
    /// </summary>
    public virtual async Task<bool> IsDownloaded(WabbajackModlistLibraryFile.ReadOnly newFile, LoadoutId loadout, CancellationToken ct)
        => (await MissingArchives(loadout, newFile, ct)).Count == 0;

    /// <summary>
    /// Starts the new list's archive downloads and returns when that batch ends, which is when <c>wabbajack status</c>
    /// stops reporting it as running. Archives that need a browser or the user's hands do not end the batch by being
    /// absent, so the caller asks <see cref="IsDownloaded"/> again afterwards. Cancelling stops the batch.
    /// </summary>
    public virtual async Task Download(WabbajackModlistLibraryFile.ReadOnly newFile, LoadoutId loadout, CancellationToken ct)
    {
        var file = LibraryFileId.From(newFile.Id);
        var info = provider.GetRequiredService<IModlistCatalog>().List().First(m => m.Id == file);
        var downloads = provider.GetRequiredService<ModlistDownloads>();
        var acquirer = provider.GetRequiredService<IArchiveAcquirer>();

        // Subscribed before the batch starts: a batch with nothing to fetch can end before Start returns.
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(AcquireStatus status)
        {
            if (status.Modlist == file && !status.IsRunning) ended.TrySetResult();
        }

        acquirer.StatusChanged += OnStatus;
        try
        {
            await downloads.Start(info, ct, loadout);
            if (acquirer.Status is { } current && current.Modlist == file && !current.IsRunning) return;

            await using var _ = ct.Register(downloads.Cancel);
            await ended.Task.WaitAsync(ct);
        }
        finally
        {
            acquirer.StatusChanged -= OnStatus;
        }
    }

    /// <summary>
    /// Reverts the loadout to the state before the update with <paramref name="revert"/> and takes back what a revert
    /// leaves behind. Refused while a job is writing into the loadout. An update that stopped halfway is undone the
    /// same way: that is its way back besides resuming it.
    /// </summary>
    /// <exception cref="ModlistUpdateRefusedException">A job is writing into the loadout, or the group has no update to undo.</exception>
    public async Task Undo(WabbajackModlistLoadoutGroup.ReadOnly group, Func<TxId, Task> revert, CancellationToken ct)
    {
        var current = Reload(group);
        var loadout = LoadoutOf(current);
        if (provider.GetRequiredService<ModlistInstallRuns>().FindInto(loadout) is not null)
            throw new ModlistUpdateRefusedException("The modlist is being installed into this loadout. Wait for it to finish, or cancel it, before undoing the update.");
        if (RestorePointOf(current) is not { } restorePoint)
            throw new ModlistUpdateRefusedException($"{NameOf(current)} has no update to undo.");

        ct.ThrowIfCancellationRequested();
        await revert(restorePoint);

        // The revert restores the rows the loadout owns, which covers the group (and with it the previous list, the
        // restore point and the switch-off note), the mod groups and the skip rows. The protection row refers to the
        // loadout without being owned by it, so the revert leaves it and it is deleted here.
        await ModlistUpdateFinish.DropProtection(Connection, loadout, restorePoint);
    }

    /// <summary>
    /// Moves <paramref name="group"/> to <paramref name="newFile"/> in place: the install job writes the restore point,
    /// switches the group and installs the new version. Every refusal happens before anything is written.
    /// Cancelling <paramref name="ct"/> stops the install job and leaves the group mid-update.
    /// </summary>
    /// <exception cref="ModlistUpdateRefusedException">The group already is at this version, the gallery has taken the list down, or archives are missing and the caller did not allow skipping them.</exception>
    public virtual async Task<WabbajackModlistLoadoutGroup.ReadOnly> Apply(
        WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile, bool skipMissing,
        IReadOnlySet<RelativePath> gameFilesToKeep, CancellationToken ct)
    {
        EnsureCanApply(group, newFile);
        var current = Reload(group);
        var loadout = LoadoutOf(current);
        if (!skipMissing) await RefuseWhenArchivesAreMissing(loadout, newFile, ct);

        ct.ThrowIfCancellationRequested();
        var job = InstallModlistJob.Create(provider, loadout, newFile, new InstallOptions(skipMissing, gameFilesToKeep, current.WabbajackModlistLoadoutGroupId));

        // The job runs on the job monitor, not on this token. Cancelling stops it where it is, which leaves the group
        // mid-update: the state Resume and Undo both start from.
        await using var stop = ct.Register(() => provider.GetRequiredService<IJobMonitor>().Cancel(job));
        return await job;
    }

    /// <summary>
    /// The refusals <see cref="Apply"/> makes before it looks at the archives: an install already writing into the
    /// loadout, a group that already is at this version, and a list the gallery has taken down. The caller asks first
    /// so it does not download for an update that will be refused.
    /// </summary>
    /// <exception cref="ModlistUpdateRefusedException">The group already is at this version, or the gallery has taken the list down.</exception>
    public virtual void EnsureCanApply(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile)
    {
        var current = Reload(group);
        provider.GetRequiredService<ModlistInstallRuns>().EnsureNoneInto(LoadoutOf(current));

        if (current.ModlistId.Value == newFile.Id && !ModlistInstallRuns.IsMidUpdate(current))
            throw new ModlistUpdateRefusedException($"{NameOf(current)} is already at this version of the list.");
        RefuseWhenTakenDown(newFile);
    }

    /// <summary>The restore point the update wrote before it switched the group, or null when none is recorded.</summary>
    public virtual TxId? RestorePointOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(Reload(group), out var restorePoint) ? TxId.From(restorePoint.Value) : null;

    /// <summary>
    /// Whether <paramref name="file"/> is not an install to offer in <paramref name="loadout"/> because a group there
    /// already has the list: the file is the version a group was switched away from, or another release (same gallery
    /// repository and machine URL) of a list a group is on. A group's own current list is never excluded, so a list
    /// whose update stopped halfway keeps its entry to resume. A file with no recorded identity is only excluded as a
    /// group's previous version.
    /// </summary>
    public static bool IsSupersededOrPendingUpdate(IDb db, WabbajackModlistLibraryFile.ReadOnly file, LoadoutId loadout)
    {
        var groups = WabbajackModlistLoadoutGroup.All(db)
            .Where(group => group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout)
            .ToArray();
        if (groups.Any(group => group.ModlistId.Value == file.Id)) return false;

        foreach (var group in groups)
        {
            if (WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(group, out var previous) && previous == file.Id) return true;

            var installed = WabbajackModlistLibraryFile.Load(db, group.ModlistId.Value);
            if (installed.IsValid() && ModlistIdentity.SameList(installed, file)) return true;
        }
        return false;
    }

    private WabbajackModlistLoadoutGroup.ReadOnly Reload(WabbajackModlistLoadoutGroup.ReadOnly group)
        => WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id);

    internal static LoadoutId LoadoutOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;

    internal static string NameOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;

    /// <summary>A list the gallery switched off is not installed, whatever its file says. Nothing is known about a list without a recorded identity or one missing from the cached listing.</summary>
    private void RefuseWhenTakenDown(WabbajackModlistLibraryFile.ReadOnly newFile)
    {
        if (!WabbajackModlistLibraryFile.MachineUrl.TryGetValue(newFile, out _)) return;

        var listing = provider.GetRequiredService<IWabbajackGalleryClient>().GetCached();
        if (listing is not null && ModlistIdentity.EntryFor(listing, newFile) is { IsForcedDown: true })
            throw new ModlistUpdateRefusedException($"{newFile.Name} has been taken down from the gallery, so it is not updated.");
    }

    private async Task<IReadOnlyList<ArchivePlanRow>> MissingArchives(LoadoutId loadout, WabbajackModlistLibraryFile.ReadOnly newFile, CancellationToken ct)
    {
        var check = await InstallModlistJob.CheckOnly(
            loadout, newFile, Connection, provider.GetRequiredService<IFileStore>(), provider.GetRequiredService<ArchiveInventory>(),
            provider.GetRequiredService<ILogger<ModlistUpdater>>(), new ModlistInstallProgress(), ct);
        return check.Archives.Missing;
    }

    private async Task RefuseWhenArchivesAreMissing(LoadoutId loadout, WabbajackModlistLibraryFile.ReadOnly newFile, CancellationToken ct)
    {
        var missing = await MissingArchives(loadout, newFile, ct);
        if (missing.Count == 0) return;

        var names = string.Join(", ", missing.Take(5).Select(row => MarkdownText.Code(row.Archive.Name))) + (missing.Count > 5 ? ", ..." : "");
        throw new ModlistUpdateRefusedException($"{missing.Count} archives are still missing: {names}; nothing was changed");
    }
}
