using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded as a package from Thunderstore.
/// </summary>
internal sealed class ThunderstoreModSource(IThunderstoreCatalogSource catalogs, ThunderstoreUpdateChecker checker, ILogger<ThunderstoreModSource>? logger = null) : IModSource
{
    public string DisplayName => "Thunderstore";

    public bool Owns(LibraryItem.ReadOnly item) => TryGetFile(item, out _);

    // Thunderstore makes no ModUpdate yet, so there is none to own.
    public bool Owns(ModUpdate update) => false;

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

    // The Thunderstore library rows read their updates from the checker and offer no Update action, so as far as
    // the shared Update column and Update All go there is nothing to apply. Each of these says so rather than
    // reporting updates that no Update action could carry out.
    public IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item) => Observable.Return(Optional<ModUpdate>.None);

    public IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> items) => Observable.Return(Optional<ModUpdate>.None);

    public IReadOnlyList<ModUpdate> AllUpdates(IGameData game) => [];

    public IObservable<bool> ObserveHasAnyUpdates(IGameData game) => Observable.Return(false);

    public ValueTask<ModUpdateOutcome> ApplyUpdates(ModUpdateRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("Thunderstore does not report updates yet, so there is nothing for it to apply.");

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
