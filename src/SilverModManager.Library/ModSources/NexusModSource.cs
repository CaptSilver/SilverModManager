using System.Collections.Concurrent;
using System.Reactive.Linq;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.MnemonicDB.Abstractions;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded from Nexus Mods.
/// </summary>
internal sealed class NexusModSource(
    IModUpdateService modUpdateService,
    ILoginManager loginManager,
    INexusUpdateDownloader downloader,
    ILibraryService libraryService,
    IConnection connection,
    ISettingsManager settingsManager,
    IOSInterop osInterop) : IModSource
{
    public string DisplayName => "Nexus Mods";

    public bool Owns(LibraryItem.ReadOnly item) => NexusModsLibraryItem.TryGet(item.Db, item.Id, out _);

    public bool Owns(ModUpdate update) => update is NexusModUpdate;

    public Uri? PageUri(LibraryItem.ReadOnly item)
    {
        if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) return null;
        var modPage = nexusItem.Value.ModPageMetadata;
        return NexusModsUrlBuilder.GetModUri(modPage.GameDomain, modPage.Uid.ModId);
    }

    // The site has no changelog page for a single file (the popup that shows one can't be linked to), only the
    // mod page's own changelog tab, so both actions open the mod page.
    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => PageUri(item);

    // The Nexus check is global, not per game, and throttles itself, so neither game nor force changes what runs.
    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
        => await modUpdateService.CheckAndUpdateModPages(cancellationToken, notify: true);

    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
    {
        if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) return Observable.Return(Optional<ModUpdate>.None);

        return modUpdateService
            .GetNewestFileVersionObservable(nexusItem.Value.FileMetadata)
            .Select(static update => AsUpdate(update.HasValue ? (ModUpdatesOnModPage)update.Value : null));
    }

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        // The items of a parent row all belong to its mod page, so any one of them names it.
        foreach (var item in items)
        {
            if (NexusModsLibraryItem.TryGet(item.Db, item.Id, out var nexusItem) is false) continue;

            return modUpdateService
                .GetNewestModPageVersionObservable(nexusItem.Value.ModPageMetadata)
                .Select(static updates => AsUpdate(updates.HasValue ? updates.Value : null));
        }
        return Observable.Return(Optional<ModUpdate>.None);
    }

    // The update service has already left out the files whose updates are hidden.
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game)
    {
        var updates = new List<ModUpdate>();
        foreach (var (_, onPage) in modUpdateService.GetAllModPagesWithUpdates(game.GameId))
        {
            if (NexusModUpdate.From(onPage) is { } update) updates.Add(update);
        }
        return updates;
    }

    // The Nexus check is global, not per game.
    public IObservable<bool> ObserveHasAnyUpdates(IGameData game) => modUpdateService.HasAnyUpdatesObservable();

    // A free account only gets links to the website's download page; nothing in a loadout is swapped.
    public bool WillReplace(ModUpdateRequest request) => request.Replace && loginManager.IsPremium;

    public async ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
    {
        // Every newest file once, with all the current files it replaces, whichever page asked for it.
        var newestToCurrent = new Dictionary<NexusModsFileMetadata.ReadOnly, List<NexusModsFileMetadata.ReadOnly>>();
        foreach (var update in request.Updates.OfType<NexusModUpdate>())
            update.Updates.NewestToCurrentFileMapping(newestToCurrent);

        // A free account can't start a download through the API; the website's download page takes it from there.
        if (loginManager.IsPremium is false)
            return request.MayOpenBrowser ? OpenInBrowser(newestToCurrent.Keys) : ModUpdateOutcome.Nothing with { AccountCannotDownload = true };

        return await DownloadAll(newestToCurrent, request, cancellationToken);
    }

    private ModUpdateOutcome OpenInBrowser(IEnumerable<NexusModsFileMetadata.ReadOnly> newestFiles)
    {
        var opened = new List<Uri>();
        foreach (var file in newestFiles)
        {
            var uri = NexusModsUrlBuilder.GetFileDownloadUri(file.ModPage.GameDomain, file.ModPage.Uid.ModId, file.Uid.FileId, useNxmLink: true);
            osInterop.OpenUri(uri);
            opened.Add(uri);
        }
        return ModUpdateOutcome.Nothing with { OpenedInBrowser = opened };
    }

    private async ValueTask<ModUpdateOutcome> DownloadAll(
        Dictionary<NexusModsFileMetadata.ReadOnly, List<NexusModsFileMetadata.ReadOnly>> newestToCurrent,
        ModUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var downloaded = new ConcurrentBag<LibraryItem.ReadOnly>();
        var replacements = new ConcurrentBag<ModUpdateReplacement>();
        var failures = new ConcurrentBag<ModUpdateFailure>();
        var limit = settingsManager.Get<DownloadSettings>().MaxParallelDownloads;
        using var semaphore = new SemaphoreSlim(limit);

        async Task Apply(NexusModsFileMetadata.ReadOnly newest, List<NexusModsFileMetadata.ReadOnly> current)
        {
            var isTaken = false;
            try
            {
                isTaken = await semaphore.WaitAsync(Timeout.Infinite, cancellationToken);
                var newItem = (await downloader.Download(newest, current, cancellationToken)).AsLibraryItem();
                downloaded.Add(newItem);
                if (request.Replace is false) return;

                foreach (var currentFile in current)
                {
                    // Should only be one library item per file.
                    foreach (var held in NexusModsLibraryItem.FindByFileMetadata(connection.Db, currentFile))
                    {
                        var old = held.AsLibraryItem();
                        var result = await libraryService.ReplaceLinkedItemsInAllLoadouts(old, newItem, new ReplaceLibraryItemOptions { IgnoreReadOnlyCollections = true });
                        replacements.Add(new ModUpdateReplacement(old, newItem, result));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The user cancelled; there is nothing to report.
            }
            catch (Exception e)
            {
                failures.Add(new ModUpdateFailure(OnlyUpdateTo(newest, request), newest.Name, e));
            }
            finally
            {
                if (isTaken) semaphore.Release();
            }
        }

        await Task.WhenAll(newestToCurrent.Select(pair => Apply(pair.Key, pair.Value)));
        return new ModUpdateOutcome([.. downloaded], [.. replacements], [.. failures], [], false);
    }

    // What is left of the batch when only the update to this file is retried.
    private static ModUpdate OnlyUpdateTo(NexusModsFileMetadata.ReadOnly newest, ModUpdateRequest request)
    {
        var mappings = request.Updates.OfType<NexusModUpdate>()
            .SelectMany(update => update.Updates.FileMappings)
            .Where(mapping => mapping.NewestFile.Id == newest.Id)
            .ToArray();
        return NexusModUpdate.From(new ModUpdatesOnModPage(mappings))
            ?? throw new InvalidOperationException($"No update in the batch leads to {newest.Name}.");
    }

    private static Optional<ModUpdate> AsUpdate(ModUpdatesOnModPage? updates)
        => updates is { } found && NexusModUpdate.From(found) is { } update ? Optional<ModUpdate>.Create(update) : Optional<ModUpdate>.None;
}
