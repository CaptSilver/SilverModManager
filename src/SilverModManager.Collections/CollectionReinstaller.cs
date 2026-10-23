using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>
/// Installs an already installed Nexus collection again from the files in the library.
/// </summary>
public static class CollectionReinstaller
{
    // The checks only hold if nothing else removes the group or starts an install between them and the removal.
    private static readonly SemaphoreSlim Gate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Removes the installed collection group and starts a fresh <see cref="InstallCollectionJob"/> for the
    /// same revision. Mods run through their installers again, so the collection's saved choices are re-applied.
    /// </summary>
    /// <remarks>
    /// The optional items that were installed stay selected; optional items that weren't are left out.
    /// Nothing is downloaded, so the group is only removed once every archive the install needs is in the library.
    /// </remarks>
    /// <param name="serviceProvider">The app's services.</param>
    /// <param name="group">The installed collection. It is reloaded, so an old handle is fine.</param>
    /// <param name="beforeRemove">Runs after the checks pass and before the group is removed, e.g. to close pages showing it.</param>
    /// <returns>The running job; the caller decides whether to await it.</returns>
    /// <exception cref="InvalidOperationException">
    /// The collection can't be reinstalled right now; the message says why. Nothing was changed.
    /// </exception>
    public static async ValueTask<IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly>> Reinstall(
        IServiceProvider serviceProvider,
        NexusCollectionLoadoutGroup.ReadOnly group,
        Func<ValueTask>? beforeRemove = null)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();

        await Gate.WaitAsync();
        try
        {
            var current = NexusCollectionLoadoutGroup.Load(connection.Db, group.Id);
            if (!current.IsValid())
                throw new InvalidOperationException("The collection is no longer installed in this loadout.");

            var collectionGroup = current.AsCollectionGroup();
            var loadoutId = collectionGroup.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
            var revision = current.Revision;
            var libraryFile = current.LibraryFile;

            // An install still running would keep adding mods to the group after it is removed.
            if (HasUnfinishedInstall(jobMonitor, revision, loadoutId))
                throw new InvalidOperationException("The collection is still being installed. Wait for it to finish, or cancel it, then try again.");

            // Which optional items count as installed is only knowable while the old group still exists.
            var installedOptional = CollectionDownloader
                .GetItems(revision, CollectionDownloader.ItemType.Optional)
                .Where(item => CollectionDownloader.GetStatus(item, collectionGroup, connection.Db).IsInstalled(out _));
            var items = CollectionDownloader
                .GetItems(revision, CollectionDownloader.ItemType.Required)
                .Concat(installedOptional)
                .ToArray();

            // The install job refuses to start with an archive missing, and by then the group would already be gone.
            var missing = items
                .Where(item => !CollectionDownloader.GetStatus(item, connection.Db).IsDownloaded())
                .Select(item => item.Name)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"These mods are no longer in the library: {string.Join(", ", missing)}. Download them again from the collection page first.");

            if (beforeRemove is not null) await beforeRemove();

            await serviceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(current.Id));

            // The job reads the group from the database, finds none, and creates a new one.
            return InstallCollectionJob.Create(serviceProvider, loadoutId, libraryFile, revision, items);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool HasUnfinishedInstall(IJobMonitor jobMonitor, CollectionRevisionMetadata.ReadOnly revision, LoadoutId loadoutId) =>
        jobMonitor.Jobs.Any(job =>
            job.Status is JobStatus.Created or JobStatus.Running or JobStatus.Paused &&
            job.Definition is InstallCollectionJob install &&
            install.RevisionMetadata.Id == revision.Id &&
            install.TargetLoadout == loadoutId);
}
