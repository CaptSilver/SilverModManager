using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData.Kernel;
using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Compares the Thunderstore packages in the library with a community's catalog. Thunderstore has no
/// per-file update feed, so this stands apart from the Nexus update service.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreUpdateChecker(IConnection connection)
{
    // One subject per community: a BehaviorSubject hands a new subscriber the current catalog and the later ones
    // atomically, so a refresh racing a subscription is never lost.
    private readonly Dictionary<string, BehaviorSubject<Optional<ThunderstoreCatalog>>> _catalogs = new();
    private readonly object _gate = new();

    /// <summary>
    /// An update for every library file of <paramref name="community"/> that has a newer active version in
    /// <paramref name="catalog"/>, leaving out frozen items.
    /// </summary>
    public IReadOnlyList<ThunderstoreUpdate> Check(string community, ThunderstoreCatalog catalog, IDb db)
    {
        var updates = new List<ThunderstoreUpdate>();
        foreach (var file in ThunderstoreLibraryFile.All(db))
        {
            if (file.Community != community) continue;
            if (Check(file, catalog, db) is { } update) updates.Add(update);
        }
        return updates;
    }

    /// <summary>
    /// Tells everything observing that <paramref name="catalog"/> is the current state of its community.
    /// </summary>
    public void Refresh(ThunderstoreCatalog catalog) => CatalogOf(catalog.Community).OnNext(Optional.Some(catalog));

    private BehaviorSubject<Optional<ThunderstoreCatalog>> CatalogOf(string community)
    {
        lock (_gate)
        {
            if (!_catalogs.TryGetValue(community, out var subject))
                _catalogs[community] = subject = new BehaviorSubject<Optional<ThunderstoreCatalog>>(Optional<ThunderstoreCatalog>.None);
            return subject;
        }
    }

    /// <summary>
    /// The update for one library file, re-evaluated on every <see cref="Refresh"/> of its community. Emits the
    /// last known state on subscribing when the community has been refreshed before, and nothing until then.
    /// </summary>
    public IObservable<Optional<ThunderstoreUpdate>> Observe(ThunderstoreLibraryFile.ReadOnly file)
    {
        var id = file.Id;

        return CatalogOf(file.Community)
            .Where(catalog => catalog.HasValue)
            .Select(catalog =>
            {
                var db = connection.Db;
                var current = ThunderstoreLibraryFile.Load(db, id);
                var update = current.IsValid() ? Check(current, catalog.Value, db) : null;
                return update is { } found ? Optional.Some(found) : Optional<ThunderstoreUpdate>.None;
            });
    }

    private static ThunderstoreUpdate? Check(ThunderstoreLibraryFile.ReadOnly file, ThunderstoreCatalog catalog, IDb db)
    {
        if (!ThunderstoreVersionNumber.TryParse(file.VersionNumber, out var installed)) return null;
        if (!ThunderstorePackageRef.TryCreate(file.Namespace, file.Name, out var package)) return null;
        if (UpdatePin.IsFrozen(db, file.Id)) return null;

        // The catalog lists versions newest first, but the order is not relied on.
        ThunderstoreVersionNumber? newest = null;
        foreach (var version in catalog.Versions(package))
        {
            if (!version.IsActive || !ThunderstoreVersionNumber.TryParse(version.VersionNumber, out var number)) continue;
            if (newest is null || number > newest) newest = number;
        }

        if (newest is not { } latest || latest <= installed) return null;
        return new ThunderstoreUpdate(file.Id, new ThunderstoreVersionRef(package, installed), new ThunderstoreVersionRef(package, latest));
    }
}
