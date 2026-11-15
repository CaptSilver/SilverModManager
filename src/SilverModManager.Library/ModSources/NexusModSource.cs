using System.Reactive.Linq;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.UpdateFilters;
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
    IOSInterop osInterop,
    IModUpdateFilterService filterService) : IModSource
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

        var steps = newestToCurrent.Select(pair => new Step(connection, downloader, pair.Key, pair.Value, OnlyUpdateTo(pair.Key, request)));
        var batch = new ModUpdateBatch(request, libraryService, cancellationToken);
        return await batch.Run(steps, settingsManager.Get<DownloadSettings>().MaxParallelDownloads);
    }

    // Every version of each file, the file itself included: the update check decides what is newer, and hiding a
    // version that is not newer yet is what keeps a later upload hidden too.
    public async ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken)
    {
        var uids = NexusItems(items)
            .Select(static item => item.FileMetadata)
            .SelectMany(RunUpdateCheck.GetAllVersionsForExistingFile)
            .Select(static file => file.Uid)
            .Distinct()
            .ToArray();
        if (uids.Length == 0) return;

        if (hidden)
            await filterService.HideFilesAsync(uids);
        else
            await filterService.ShowFilesAsync(uids);
    }

    // The update observables used here are the unfiltered ones: a row whose updates are all hidden has to keep its
    // action, or the user could never show them again.
    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var nexusItems = NexusItems(items).ToList();
        if (nexusItems.Count == 0) return Observable.Return(NoHideAction);

        var state = nexusItems.Count == 1
            ? ObserveFileHideState(nexusItems[0].FileMetadata)
            : ObservePageHideState(nexusItems[0].ModPageMetadata);
        return state.DistinctUntilChanged();
    }

    public HideUpdatesLabelKind HideUpdatesLabel(bool hidden) => hidden ? HideUpdatesLabelKind.Show : HideUpdatesLabelKind.Hide;

    private static readonly HideUpdatesState NoHideAction = new(IsHidden: false, ItemCount: 0, IsEnabled: false, IsVisible: false);

    // Read from the current database rather than the items' own: a row's items date from when it was built, and the
    // newer files the update check found since are only in the current one.
    private IEnumerable<NexusModsLibraryItem.ReadOnly> NexusItems(IEnumerable<LibraryItem.ReadOnly> items)
    {
        foreach (var item in items)
        {
            if (NexusModsLibraryItem.TryGet(connection.Db, item.Id, out var nexusItem)) yield return nexusItem.Value;
        }
    }

    // Hidden when every newer file is. The button is only there while an unfiltered update exists, so the view
    // can't be hiding the fact that there is nothing to hide.
    private IObservable<HideUpdatesState> ObserveFileHideState(NexusModsFileMetadata.ReadOnly file)
        => modUpdateService.GetNewestFileVersionObservableUnfiltered(file)
            .CombineLatest(filterService.FilterTrigger.StartWith(System.Reactive.Unit.Default), static (update, _) => update)
            .Select(update => update.HasValue
                ? new HideUpdatesState(update.Value.NewerFiles.All(newer => filterService.IsFileHidden(newer.Uid)), 1, IsEnabled: true, IsVisible: true)
                : NoHideAction with { ItemCount = 1 });

    // When any file's updates are hidden the page offers to show them (all of them); the
    // count is the files that action affects.
    private IObservable<HideUpdatesState> ObservePageHideState(NexusModsModPageMetadata.ReadOnly page)
        => modUpdateService.GetNewestModPageVersionObservableUnfiltered(page)
            .CombineLatest(filterService.FilterTrigger.StartWith(System.Reactive.Unit.Default), static (updates, _) => updates)
            .Select(updates =>
            {
                if (updates.HasValue is false) return NoHideAction;

                var (hidden, shown) = (0, 0);
                foreach (var mapping in updates.Value.FileMappings)
                {
                    if (mapping.NewerFiles.All(newer => filterService.IsFileHidden(newer.Uid))) hidden++;
                    else shown++;
                }

                var hasUpdates = updates.Value.HasAnyUpdates;
                return new HideUpdatesState(hidden > 0, hidden > 0 ? hidden : shown, hasUpdates, hasUpdates);
            });

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

    // One newest file with every current file it replaces, whichever page asked for it.
    private sealed class Step(
        IConnection connection,
        INexusUpdateDownloader downloader,
        NexusModsFileMetadata.ReadOnly newest,
        List<NexusModsFileMetadata.ReadOnly> currentFiles,
        ModUpdate update) : DownloadThenReplaceStep(update, newest.Name)
    {
        // Should only be one library item per file.
        protected override IReadOnlyList<LibraryItem.ReadOnly> LoadCurrent()
            => [.. currentFiles.SelectMany(file => NexusModsLibraryItem.FindByFileMetadata(connection.Db, file)).Select(static held => held.AsLibraryItem())];

        // The downloader wants the files' metadata, which is what the current items were found by.
        protected override async Task<LibraryItem.ReadOnly?> Download(IReadOnlyList<LibraryItem.ReadOnly> current, ModUpdateBatch batch)
            => (await downloader.Download(newest, currentFiles, batch.CancellationToken)).AsLibraryItem();
    }

    // What is left of the batch when only the update to this file is retried. Every newest file came from the
    // request's own mappings, so the lookup below cannot come up empty.
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
