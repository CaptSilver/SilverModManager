using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Collections;

internal static class Verbs
{
    internal static IServiceCollection AddCollectionVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("collection", "Commands for the Nexus collections installed in a loadout")
            .AddVerb(() => InstallCollection)
            .AddVerb(() => RepairCollections);

    [Verb("install-collection", "Installs a collection into the given loadout")]
    private static async Task<int> InstallCollection([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to install the collection into")] Loadout.ReadOnly loadout,
        [Option("s", "slug", "Collection slug")] string slug,
        [Option("r", "revision", "Collection revision")] int revision,
        [Injected] TemporaryFileManager temporaryFileManager,
        [Injected] ILibraryService libraryService,
        [Injected] NexusModsLibrary nexusModsLibrary,
        [Injected] IServiceProvider serviceProvider,
        [Injected] ILoginManager loginManager,
        [Injected] IConnection connection,
        [Injected] CollectionDownloader collectionDownloader,
        [Injected] CancellationToken token)
    {
        await using var destination = temporaryFileManager.CreateFile();
        var downloadJob = nexusModsLibrary.CreateCollectionDownloadJob(destination, CollectionSlug.From(slug), RevisionNumber.From((ulong)revision), token);
        
        var libraryFile = await libraryService.AddDownload(downloadJob);

        if (!libraryFile.TryGetAsNexusModsCollectionLibraryFile(out var collectionFile))
            throw new InvalidOperationException("The library file is not a NexusModsCollectionLibraryFile");

        var revisionMetadata = await nexusModsLibrary.GetOrAddCollectionRevision(collectionFile, CollectionSlug.From(slug), RevisionNumber.From((ulong)revision), token);

        if (loginManager.IsPremium)
        {
            await collectionDownloader.DownloadItems(revisionMetadata, itemType: CollectionDownloader.ItemType.Required, db: connection.Db,
                cancellationToken: token
            );
        }
        else
        {
            var tuples = collectionDownloader.GetMissingDownloadLinks(revisionMetadata, db: connection.Db, itemType: CollectionDownloader.ItemType.Required);
            if (tuples.Count > 0)
            {
                await renderer.TextLine($"Missing {tuples.Count} downloads:");
                await renderer.Table(["Name", "Uri"], tuples.Select(t => new object[] { t.Download.Name, t.Uri.ToString() }));
                return 0;
            }
        }

        var items = CollectionDownloader.GetItems(revisionMetadata, CollectionDownloader.ItemType.Required);
        await InstallCollectionJob.Create(serviceProvider, loadout, collectionFile, revisionMetadata, items);
        return 0;
    }

    [Verb("collection repair", "Makes each Nexus collection in the loadout hold exactly the mods of the revision it is on: removes old versions an update left behind and moves mods the revision doesn't list to My Mods")]
    private static async Task<int> RepairCollections([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout whose collections to repair")] Loadout.ReadOnly loadout,
        [Option("c", "collection", "Only the collection with this slug", true)] string? slug,
        [Option("n", "dry-run", "Print what would change without changing anything", true)] bool dryRun,
        [Injected] IServiceProvider serviceProvider,
        [Injected] IConnection connection,
        [Injected] CancellationToken token)
    {
        var groups = NexusCollectionLoadoutGroup.All(connection.Db)
            .Where(group => group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout.LoadoutId)
            .Where(group => string.IsNullOrEmpty(slug) || group.Collection.Slug.Value == slug)
            .ToArray();
        if (groups.Length == 0)
        {
            await renderer.TextLine(string.IsNullOrEmpty(slug) ? "The loadout has no Nexus collections." : $"The loadout has no collection `{slug}`.");
            return string.IsNullOrEmpty(slug) ? 0 : 1;
        }

        foreach (var group in groups)
        {
            var name = $"{group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name} (`{group.Collection.Slug}`, revision {group.Revision.RevisionNumber})";
            var result = await CollectionUpdater.Repair(serviceProvider, group, dryRun, token);
            foreach (var line in result.Describe(name)) await renderer.TextLine(line);
        }

        return 0;
    }
}
