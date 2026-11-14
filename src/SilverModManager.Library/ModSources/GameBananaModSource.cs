using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.GameBanana;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded from a GameBanana mod page.
/// </summary>
/// <remarks>
/// A mod has an update when the page's update stamp is newer than the one the item was downloaded at and the page lists
/// a file the library does not hold. The page's modified stamp is ignored: it moves on any edit, a description included.
/// The check asks the site once per mod, on entering the page and on Refresh, and keeps the answer for an hour. The file
/// is fetched directly, so Update All never opens a browser.
/// </remarks>
internal sealed class GameBananaModSource(
    GameBananaProfiles profiles,
    IConnection connection,
    IServiceProvider provider,
    Func<HttpClient> client,
    ILogger<GameBananaModSource> logger) : IModSource
{
    public string DisplayName => "GameBanana";

    public bool Owns(LibraryItem.ReadOnly item) => TryGetFile(item.Id, out _);

    public bool Owns(ModUpdate update) => update is GameBananaModUpdate;

    public Uri? PageUri(LibraryItem.ReadOnly item) => TryGetFile(item.Id, out var file) && file.ModId > 0 ? new Uri($"https://gamebanana.com/mods/{file.ModId}") : null;

    public Uri? ChangelogUri(LibraryItem.ReadOnly item) => PageUri(item);

    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
    {
        if (GameBananaGame.For(game) is not { } support) return;

        foreach (var modId in FilesOf(support.GameBananaGameId).Select(static file => file.ModId).Distinct().ToArray())
        {
            try
            {
                await profiles.Get(modId, force, cancellationToken);
            }
            catch (GameBananaRateLimitedException e)
            {
                // The site asked to be left alone; asking about the rest would only make it longer.
                logger.LogWarning("Not checking the rest of the GameBanana mods for updates: {Reason}", e.Message);
                break;
            }
            catch (Exception e) when (e is UserFacingException or HttpRequestException)
            {
                logger.LogWarning("Not checking GameBanana mod {ModId} for an update: {Reason}", modId, e.Message);
            }
        }

        profiles.NotifyChanged();
    }

    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
        => profiles.Changed.StartWith(Unit.Default).Select(_ => AsOptional(UpdateFor([item.Id])));

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Select(static item => item.Id).ToArray();
        return profiles.Changed.StartWith(Unit.Default).Select(_ => AsOptional(UpdateFor(ids)));
    }

    // One update per mod, the way one Thunderstore package is one update.
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game)
        => GameBananaGame.For(game) is { } support ? [.. Updates(FilesOf(support.GameBananaGameId).Select(static file => file.Id))] : [];

    public IObservable<bool> ObserveHasAnyUpdates(IGameData game)
        => profiles.Changed.StartWith(Unit.Default).Select(_ => AllUpdates(game).Count > 0).DistinctUntilChanged();

    // Both a replace and a keep-old download; only a replace changes what a loadout links.
    public bool WillReplace(ModUpdateRequest request) => request.Replace;

    public async ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
    {
        var downloaded = new List<LibraryItem.ReadOnly>();
        var replacements = new List<ModUpdateReplacement>();
        var failures = new List<ModUpdateFailure>();

        foreach (var update in request.Updates.OfType<GameBananaModUpdate>())
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var current = update.CurrentItems.Select(id => LibraryItem.Load(connection.Db, id)).Where(static item => item.IsValid()).ToArray();
                if (current.Length == 0) throw new InvalidOperationException($"{update.Profile.Name} {update.CurrentVersion} is no longer in the library.");

                var added = await Download(update, current, cancellationToken);
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
                failures.Add(new ModUpdateFailure(update, update.Profile.Name, e));
            }
        }

        // The files are fetched by id from the site's own download address: nothing here opens a browser.
        return new ModUpdateOutcome(downloaded, replacements, failures, [], false);
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
        profiles.NotifyChanged();
    }

    // Always enabled for an item of this source: a frozen item has no update, and tying the action to one would take
    // away the only way to unfreeze it.
    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Where(item => TryGetFile(item.Id, out _)).Select(static item => item.Id).ToArray();
        if (ids.Length == 0) return Observable.Return(new HideUpdatesState(IsHidden: false, ItemCount: 0, IsEnabled: false, IsVisible: false));

        return profiles.Changed.StartWith(Unit.Default)
            .Select(_ => new HideUpdatesState(ids.All(id => UpdatePin.IsFrozen(connection.Db, id)), ids.Length, IsEnabled: true, IsVisible: true))
            .DistinctUntilChanged();
    }

    public HideUpdatesLabelKind HideUpdatesLabel(bool hidden) => hidden ? HideUpdatesLabelKind.Unfreeze : HideUpdatesLabelKind.Freeze;

    private bool TryGetFile(EntityId id, out GameBananaLibraryFile.ReadOnly file)
    {
        file = GameBananaLibraryFile.Load(connection.Db, id);
        return file.IsValid();
    }

    // A file restored from a lock has no mod page to ask about.
    private IEnumerable<GameBananaLibraryFile.ReadOnly> FilesOf(int gameBananaGameId)
        => GameBananaLibraryFile.All(connection.Db).Where(file => file.GameId == gameBananaGameId && file.ModId > 0);

    private static Optional<ModUpdate> AsOptional(GameBananaModUpdate? update) => update is null ? Optional<ModUpdate>.None : Optional<ModUpdate>.Create(update);

    private GameBananaModUpdate? UpdateFor(IReadOnlyList<EntityId> ids) => Updates(ids).FirstOrDefault();

    private IEnumerable<GameBananaModUpdate> Updates(IEnumerable<EntityId> ids)
    {
        var byMod = new Dictionary<long, (GameBananaProfile Profile, GameBananaFile File, List<(LibraryItem.ReadOnly Item, string Version)> Items)>();
        var held = new Dictionary<long, HashSet<long>>();
        foreach (var id in ids)
        {
            if (!TryGetFile(id, out var file) || file.ModId <= 0 || UpdatePin.IsFrozen(connection.Db, id)) continue;
            if (profiles.Peek(file.ModId) is not { } profile || profile.UpdateStamp <= file.DateUpdated) continue;

            if (!held.TryGetValue(file.ModId, out var heldIds))
                held[file.ModId] = heldIds = [.. GameBananaLibraryFile.All(connection.Db).Where(other => other.ModId == file.ModId).Select(static other => other.FileId)];

            // The newest file the library lacks: a page that only had its text or a held file touched is no update.
            var newest = profile.AllFiles.Where(candidate => !heldIds.Contains(candidate.Id)).OrderByDescending(static candidate => candidate.DateAdded ?? 0).ThenByDescending(static candidate => candidate.Id).FirstOrDefault();
            if (newest is null) continue;

            if (!byMod.TryGetValue(file.ModId, out var group)) byMod[file.ModId] = group = (profile, newest, []);
            group.Items.Add((LibraryItem.Load(connection.Db, id), GameBananaVersions.Held(file)));
        }

        foreach (var (modId, (profile, file, items)) in byMod)
        {
            if (GameBananaModUpdate.From(modId, profile, file, items) is { } update) yield return update;
        }
    }

    private async Task<LibraryItem.ReadOnly> Download(GameBananaModUpdate update, IReadOnlyList<LibraryItem.ReadOnly> current, CancellationToken cancellationToken)
    {
        var profile = update.Profile;
        var page = Uri.TryCreate(profile.ProfileUrl, UriKind.Absolute, out var listed) ? listed : new Uri($"https://gamebanana.com/mods/{profile.Id}");
        if (!update.File.IsClean)
            throw new GameBananaRefusedException($"GameBanana's virus scan did not clear {MarkdownText.Code(update.File.FileName)}.", page, profile.AlternateFileSources);
        if (profile.LicenseChecklist?.ForbidsInstalling == true)
            throw new GameBananaRefusedException("The author does not allow installing this mod.", page, profile.AlternateFileSources);

        var gameId = GameBananaLibraryFile.Load(connection.Db, current[0].Id).GameId;
        var job = AddGameBananaFileJob.Create(
            provider, client(), update.ModId, update.File, page, new UpdateAcquisition(current.Select(static item => LibraryItemId.From(item.Id))),
            gameId, profile.UpdateStamp, profile.Version);

        var monitor = provider.GetRequiredService<SilverModManager.Sdk.Jobs.IJobMonitor>();
        await using var registration = cancellationToken.Register(() => monitor.Cancel(job));
        return (await job).AsLibraryFile().AsLibraryItem();
    }
}
