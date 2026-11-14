using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.ModIo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded from a mod.io mod.
/// </summary>
/// <remarks>
/// A mod has an update when mod.io's update stamp is newer than the one the item was downloaded at and the mod's current
/// modfile is one the library does not hold; an edit to the description moves the stamp and nothing else, and is no
/// update. The check asks mod.io once per mod, on entering the page and on Refresh, and keeps the answer for an hour.
/// A game whose mod manager syncs subscriptions has no items here: the mods it downloads are not the app's files.
/// When mod.io will not hand the file to the app, Update All opens nothing and reports that the account cannot download;
/// only a single update the user started opens the mod's page and waits for the file in the Downloads folder.
/// </remarks>
internal sealed class ModIoModSource(
    ModIoMods mods,
    IConnection connection,
    IServiceProvider provider,
    Func<HttpClient> client,
    ModIoPickups pickups,
    IOSInterop osInterop,
    ILogger<ModIoModSource> logger) : IModSource
{
    public string DisplayName => "mod.io";

    public bool Owns(LibraryItem.ReadOnly item) => TryGetFile(item.Id, out _);

    public bool Owns(ModUpdate update) => update is ModIoModUpdate;

    public Uri? PageUri(LibraryItem.ReadOnly item)
        => TryGetFile(item.Id, out var file) && mods.Peek(file.GameId, file.ModId)?.ProfileUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var page) ? page : null;

    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => PageUri(item);

    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
    {
        if (ModIoGame.For(game) is not { } support) return;

        foreach (var modId in FilesOf(support.ModIoGameId).Select(static file => file.ModId).Distinct().ToArray())
        {
            try
            {
                await mods.Get(support.ModIoGameId, modId, force, cancellationToken);
            }
            catch (ModIoRateLimitedException e)
            {
                // mod.io asked to be left alone, and continuing can cost the token; asking about the rest would only make it worse.
                logger.LogWarning("Not checking the rest of the mod.io mods for updates: {Reason}", e.Message);
                break;
            }
            catch (ModIoNotSignedInException e)
            {
                logger.LogInformation("Not checking mod.io mods for updates: {Reason}", e.Message);
                break;
            }
            catch (Exception e) when (e is UserFacingException or HttpRequestException)
            {
                logger.LogWarning("Not checking mod.io mod {ModId} for an update: {Reason}", modId, e.Message);
            }
        }

        mods.NotifyChanged();
    }

    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
        => mods.Changed.StartWith(Unit.Default).Select(_ => AsOptional(UpdateFor([item.Id])));

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Select(static item => item.Id).ToArray();
        return mods.Changed.StartWith(Unit.Default).Select(_ => AsOptional(UpdateFor(ids)));
    }

    // One update per mod, the way one Thunderstore package is one update.
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game)
        => ModIoGame.For(game) is { } support ? [.. Updates(FilesOf(support.ModIoGameId).Select(static file => file.Id))] : [];

    public IObservable<bool> ObserveHasAnyUpdates(IGameData game)
        => mods.Changed.StartWith(Unit.Default).Select(_ => AllUpdates(game).Count > 0).DistinctUntilChanged();

    // Both a replace and a keep-old download; only a replace changes what a loadout links.
    public bool WillReplace(ModUpdateRequest request) => request.Replace;

    public async ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
    {
        var downloaded = new List<LibraryItem.ReadOnly>();
        var replacements = new List<ModUpdateReplacement>();
        var failures = new List<ModUpdateFailure>();
        var opened = new List<Uri>();
        var cannotDownload = false;

        foreach (var update in request.Updates.OfType<ModIoModUpdate>())
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var current = update.CurrentItems.Select(id => LibraryItem.Load(connection.Db, id)).Where(static item => item.IsValid()).ToArray();
                if (current.Length == 0) throw new InvalidOperationException($"{update.Mod.Name} {update.CurrentVersion} is no longer in the library.");

                var downloadedItem = await Download(update, current, request.MayOpenBrowser, opened, cancellationToken);
                if (downloadedItem is not { } added)
                {
                    cannotDownload |= !request.MayOpenBrowser;
                    continue;
                }

                downloaded.Add(added);
                if (!request.Replace) continue;

                var library = provider.GetRequiredService<ILibraryService>();
                foreach (var old in current)
                {
                    var result = await library.ReplaceLinkedItemsInAllLoadouts(old, added, new ReplaceLibraryItemOptions { SkipLockedCollections = true, KeepPlace = true });
                    replacements.Add(new ModUpdateReplacement(old, added, result));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The user cancelled; there is nothing to report.
            }
            catch (Exception e)
            {
                failures.Add(new ModUpdateFailure(update, update.Mod.Name, e));
            }
        }

        return new ModUpdateOutcome(downloaded, replacements, failures, opened, cannotDownload);
    }

    // Hiding an update means freezing the item: the check leaves a frozen item out. Unfreezing retracts the flag
    // instead of writing false, so an item that was never frozen carries nothing.
    public async ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction();
        var changed = false;
        foreach (var item in items)
        {
            if (!TryGetFile(item.Id, out _)) continue;

            var isFrozen = UpdatePin.IsFrozen(connection.Db, item.Id);
            if (hidden && !isFrozen)
            {
                _ = new UpdatePin.New(tx, item.Id) { Frozen = true };
                changed = true;
            }
            else if (!hidden && isFrozen)
            {
                tx.Retract(item.Id, UpdatePin.Frozen, true);
                changed = true;
            }
        }

        if (!changed) return;

        await tx.Commit();
        mods.NotifyChanged();
    }

    // Always enabled for an item of this source: a frozen item has no update, and tying the action to one would take
    // away the only way to unfreeze it.
    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Where(item => TryGetFile(item.Id, out _)).Select(static item => item.Id).ToArray();
        if (ids.Length == 0) return Observable.Return(new HideUpdatesState(IsHidden: false, ItemCount: 0, IsEnabled: false, IsVisible: false));

        return mods.Changed.StartWith(Unit.Default)
            .Select(_ => new HideUpdatesState(ids.All(id => UpdatePin.IsFrozen(connection.Db, id)), ids.Length, IsEnabled: true, IsVisible: true))
            .DistinctUntilChanged();
    }

    public HideUpdatesLabelKind HideUpdatesLabel(bool hidden) => hidden ? HideUpdatesLabelKind.Unfreeze : HideUpdatesLabelKind.Freeze;

    private bool TryGetFile(EntityId id, out ModIoLibraryFile.ReadOnly file)
    {
        file = ModIoLibraryFile.Load(connection.Db, id);
        return file.IsValid();
    }

    private IEnumerable<ModIoLibraryFile.ReadOnly> FilesOf(uint gameId)
        => ModIoLibraryFile.All(connection.Db).Where(file => file.GameId == gameId);

    private static Optional<ModUpdate> AsOptional(ModIoModUpdate? update) => update is null ? Optional<ModUpdate>.None : Optional<ModUpdate>.Create(update);

    private ModIoModUpdate? UpdateFor(IReadOnlyList<EntityId> ids) => Updates(ids).FirstOrDefault();

    private IEnumerable<ModIoModUpdate> Updates(IEnumerable<EntityId> ids)
    {
        var byMod = new Dictionary<(uint Game, uint Mod), (ModIoMod Mod, ModIoFile File, List<(LibraryItem.ReadOnly Item, string Version)> Items)>();
        var held = new Dictionary<(uint Game, uint Mod), HashSet<uint>>();
        foreach (var id in ids)
        {
            if (!TryGetFile(id, out var file) || UpdatePin.IsFrozen(connection.Db, id)) continue;
            if (mods.Peek(file.GameId, file.ModId) is not { Modfile: { } newest } mod || mod.DateUpdated <= file.DateUpdated) continue;

            var key = (file.GameId, file.ModId);
            if (!held.TryGetValue(key, out var heldIds))
                held[key] = heldIds = [.. ModIoLibraryFile.All(connection.Db).Where(other => other.GameId == file.GameId && other.ModId == file.ModId).Select(static other => other.FileId)];

            // A mod whose description was edited, or whose current modfile the library already holds, is no update.
            if (heldIds.Contains(newest.Id)) continue;

            if (!byMod.TryGetValue(key, out var group)) byMod[key] = group = (mod, newest, []);
            group.Items.Add((LibraryItem.Load(connection.Db, id), ModIoFiles.Held(file)));
        }

        foreach (var ((gameId, _), (mod, file, items)) in byMod)
        {
            if (ModIoModUpdate.From(gameId, mod, file, items) is { } update) yield return update;
        }
    }

    // Null when mod.io would not hand the file to the app: the caller says so, or this opened the page.
    private async Task<LibraryItem.ReadOnly?> Download(
        ModIoModUpdate update, IReadOnlyList<LibraryItem.ReadOnly> current, bool mayOpenBrowser, List<Uri> opened, CancellationToken cancellationToken)
    {
        // The address cached with the mod expires, so the mod is read again.
        var mod = await mods.Get(update.GameId, update.ModId, force: true, cancellationToken)
            ?? throw new ModIoInstallRefusedException($"mod.io no longer has {MarkdownText.Inline(update.Mod.Name)}.", null);
        var file = mod.Modfile ?? throw new ModIoInstallRefusedException($"{MarkdownText.Inline(mod.Name)} has no file on mod.io now.", null);
        var page = Uri.TryCreate(mod.ProfileUrl, UriKind.Absolute, out var listed) ? listed : new Uri("https://mod.io/");
        if (file.ScanProblem is { } problem) throw new ModIoInstallRefusedException($"{MarkdownText.Code(file.FileName)} was not installed: mod.io's virus scan {problem}.", page);

        var acquisition = new UpdateAcquisition(current.Select(static item => LibraryItemId.From(item.Id)));
        var job = AddModIoFileJob.Create(provider, client(), update.GameId, mod.Id, file, page, acquisition, mod.DateUpdated);
        var monitor = provider.GetRequiredService<SilverModManager.Sdk.Jobs.IJobMonitor>();
        await using var registration = cancellationToken.Register(() => monitor.Cancel(job));
        try
        {
            return (await job).AsLibraryFile().AsLibraryItem();
        }
        catch (Exception e) when (ModIoFiles.IsRejectedAddress(e))
        {
            if (!mayOpenBrowser) return null;

            // The user asked for this one update: the page opens and the file is picked up from the Downloads folder.
            pickups.Arm(update.GameId, mod.Id, file, mod.DateUpdated, acquisition);
            osInterop.OpenUri(page);
            opened.Add(page);
            return null;
        }
    }
}
