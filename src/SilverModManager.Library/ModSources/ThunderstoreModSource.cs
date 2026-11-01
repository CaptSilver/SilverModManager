using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded as a package from Thunderstore.
/// </summary>
internal sealed class ThunderstoreModSource(
    IThunderstoreCatalogSource catalogs,
    ThunderstoreUpdateChecker checker,
    IConnection connection,
    IThunderstoreInstaller installer,
    IThunderstoreSource thunderstoreSource,
    IEnumerable<IGameData> games,
    ILogger<ThunderstoreModSource>? logger = null) : IModSource
{
    public string DisplayName => "Thunderstore";

    public bool Owns(LibraryItem.ReadOnly item) => TryGetFile(item, out _);

    public bool Owns(ModUpdate update) => update is ThunderstoreModUpdate;

    public Uri? PageUri(LibraryItem.ReadOnly item)
        => TryGetPackage(item, out var community, out var package) ? ThunderstoreUrls.PackagePage(community, package) : null;

    public Uri? ChangelogUri(LibraryItem.ReadOnly item)
        => TryGetPackage(item, out var community, out var package) ? ThunderstoreUrls.Changelog(community, package) : null;

    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
    {
        if (ThunderstoreGame.For(game) is not { } thunderstoreGame) return;

        var community = thunderstoreGame.ThunderstoreCommunity;
        if (force || catalogs.IsStale(community))
        {
            // A failed refresh leaves the previous listing loadable, so offline still compares against it.
            // A timeout surfaces as an OperationCanceledException, so only the caller's token means "stop".
            try
            {
                await catalogs.Refresh(community, cancellationToken);
            }
            catch (Exception e) when (cancellationToken.IsCancellationRequested is false)
            {
                logger?.LogWarning(e, "Refreshing the Thunderstore catalog for {Community} failed; using the cached one", community);
            }
        }

        // The catalog is loaded even when it was not refreshed: the checker starts empty each run, and the
        // cached listing is what it compares the library against.
        checker.Refresh(await catalogs.Load(community, cancellationToken));
    }

    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
    {
        if (TryGetFile(item, out var file) is false) return Observable.Return(Optional<ModUpdate>.None);

        return checker.Observe(file).Select(static update => AsUpdate(update.HasValue ? [update.Value] : []));
    }

    // The package row's items are the versions downloaded when the row was built; the row asks again when it gains
    // or loses one. A version in a community that has not been checked yet has no update, which must not hold up
    // the ones in a community that has.
    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var observed = new List<IObservable<Optional<ThunderstoreUpdate>>>();
        foreach (var item in items)
        {
            if (TryGetFile(item, out var file)) observed.Add(checker.Observe(file).StartWith(Optional<ThunderstoreUpdate>.None));
        }
        if (observed.Count == 0) return Observable.Return(Optional<ModUpdate>.None);

        return observed.CombineLatest().Select(static updates => AsUpdate([.. updates.Where(static update => update.HasValue).Select(static update => update.Value)]));
    }

    // One update per package, the way one Nexus mod page is one update.
    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game)
    {
        if (ThunderstoreGame.For(game) is not { } thunderstoreGame) return [];

        return [.. checker.Current(thunderstoreGame.ThunderstoreCommunity)
            .GroupBy(static update => update.Newest.Package)
            .Select(static package => ThunderstoreModUpdate.From([.. package]))
            .OfType<ModUpdate>()];
    }

    public IObservable<bool> ObserveHasAnyUpdates(IGameData game)
        => ThunderstoreGame.For(game) is { } thunderstoreGame
            ? checker.ObserveCommunity(thunderstoreGame.ThunderstoreCommunity).Select(static updates => updates.Count > 0)
            : Observable.Return(false);

    // Both a replace and a keep-old download; only a replace changes what a loadout links.
    public bool WillReplace(ModUpdateRequest request) => request.Replace;

    public async ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
    {
        var downloaded = new Dictionary<EntityId, LibraryItem.ReadOnly>();
        var replacements = new List<ModUpdateReplacement>();
        var failures = new List<ModUpdateFailure>();

        async Task Apply(ThunderstoreModUpdate package, IReadOnlyList<ThunderstoreUpdate> updates, Func<Task> apply)
        {
            try
            {
                await apply();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The user cancelled; there is nothing to report.
            }
            catch (Exception e)
            {
                failures.Add(new ModUpdateFailure(ThunderstoreModUpdate.From(updates) ?? package, package.Newest.ToString(), e));
            }
        }

        foreach (var package in request.Updates.OfType<ThunderstoreModUpdate>())
        {
            // Apply swallows the user's cancellation, so the loops stop here instead of entering each remaining
            // download with a token that is already cancelled.
            if (cancellationToken.IsCancellationRequested) break;

            if (request.Replace)
            {
                foreach (var update in package.Updates)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    await Apply(package, [update], async () =>
                    {
                        var current = LibraryItem.Load(connection.Db, update.LibraryFileId);
                        if (current.IsValid() is false) throw new InvalidOperationException($"{update.Current} is no longer in the library.");

                        var outcome = await installer.UpdateAsync(update.Newest, current, cancellationToken);
                        foreach (var item in outcome.Downloaded) downloaded[item.Id] = item;
                        replacements.AddRange(outcome.Replacements);
                    });
                }
            }
            else
            {
                await Apply(package, package.Updates, async () =>
                {
                    foreach (var file in await AddToLibrary(package, cancellationToken))
                        downloaded[file.Id] = file.AsLibraryItem();
                });
            }
        }

        return new ModUpdateOutcome([.. downloaded.Values], replacements, failures, [], false);
    }

    // Hiding an update means freezing the version: the update check leaves a frozen item out and the lock keeps it
    // where it is. Unfreezing retracts the flag instead of writing false, so an item that was never frozen carries
    // nothing.
    public async ValueTask SetUpdatesHidden(IReadOnlyList<LibraryItem.ReadOnly> items, bool hidden, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction();
        var changed = false;
        foreach (var item in items)
        {
            if (TryGetFile(item, out _) is false) continue;

            var isFrozen = UpdatePin.IsFrozen(connection.Db, item.Id);
            if (hidden && isFrozen is false)
            {
                _ = new UpdatePin.New(tx, item.Id) { Frozen = true };
                changed = true;
            }
            else if (hidden is false && isFrozen)
            {
                tx.Retract(item.Id, UpdatePin.Frozen, true);
                changed = true;
            }
        }

        if (changed) await tx.Commit();
    }

    // Always enabled and visible for an item of this source: the frozen item has no update, so tying the action to
    // one would take away the only way to unfreeze it. Freezing also makes sense before any update exists.
    public IObservable<HideUpdatesState> ObserveHideUpdates(IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var ids = items.Where(static item => TryGetFile(item, out _)).Select(static item => item.Id).ToArray();
        if (ids.Length == 0) return Observable.Return(new HideUpdatesState(IsHidden: false, ItemCount: 0, IsEnabled: false, IsVisible: false));

        return checker.ObserveFrozenChanges()
            .Select(_ => new HideUpdatesState(ids.All(id => UpdatePin.IsFrozen(connection.Db, id)), ids.Length, IsEnabled: true, IsVisible: true))
            .DistinctUntilChanged();
    }

    public string HideUpdatesLabel(bool hidden) => hidden ? "Unfreeze version" : "Freeze version";

    // Library only: nothing is linked, so the old version stays in the loadouts.
    private async Task<IReadOnlyList<LibraryFile.ReadOnly>> AddToLibrary(ThunderstoreModUpdate package, CancellationToken cancellationToken)
    {
        var community = ThunderstoreLibraryFile.Load(connection.Db, package.Updates[0].LibraryFileId).Community;
        var game = ThunderstoreGame.ForCommunity(games, community) ?? throw new NoThunderstoreGameException(community);

        var request = new ThunderstoreInstallRequest(package.Newest, game, WithDependencies: true, DependencyPolicy.Latest, ExpectedSha256: null, new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>());
        return await thunderstoreSource.AddAsync(request, cancellationToken);
    }

    private static Optional<ModUpdate> AsUpdate(IReadOnlyList<ThunderstoreUpdate> updates)
        => ThunderstoreModUpdate.From(updates) is { } update ? Optional<ModUpdate>.Create(update) : Optional<ModUpdate>.None;

    private static bool TryGetFile(LibraryItem.ReadOnly item, out ThunderstoreLibraryFile.ReadOnly file)
    {
        file = default;
        return item.TryGetAsLibraryFile(out var libraryFile) && libraryFile.TryGetAsThunderstoreLibraryFile(out file);
    }

    // A row whose team or package name Thunderstore would not accept is owned but has no address to build.
    private static bool TryGetPackage(LibraryItem.ReadOnly item, out string community, out ThunderstorePackageRef package)
    {
        community = "";
        package = default;
        if (TryGetFile(item, out var file) is false) return false;

        community = file.Community;
        return ThunderstorePackageRef.TryCreate(file.Namespace, file.Name, out package);
    }
}
