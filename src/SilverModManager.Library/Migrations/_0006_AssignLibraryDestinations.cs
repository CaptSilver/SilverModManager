using Microsoft.Extensions.Logging;
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Migrations;

/// <summary>
/// Gives every library item that was added before destinations were recorded a game, from the first evidence that has
/// an answer, or marks it unknown.
/// </summary>
/// <remarks>
/// The migration knows nothing about sources; it asks each <see cref="IDestinationInference"/> in priority order. A member
/// of an archive is skipped, because it shows with the archive that holds it and marking each one unknown would put every
/// member of every archive on every game's page. An item that already has games or the unknown marker is skipped, so
/// a second run does nothing.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0006_AssignLibraryDestinations(IEnumerable<IDestinationInference> inferences, ILogger<_0006_AssignLibraryDestinations> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0006_AssignLibraryDestinations));

    private readonly List<(EntityId Item, Destination Destination)> _assignments = [];

    public Task Prepare(IDb db)
    {
        _assignments.Clear();
        var ordered = inferences.OrderBy(inference => inference.Priority).ToArray();
        foreach (var datom in db.Datoms(LibraryItem.Name))
        {
            var item = LibraryItem.Load(db, datom.E);
            if (!item.IsValid() || item.DestinationGames.Any() || item.Contains(LibraryItem.DestinationUnknown)) continue;
            if (LibraryArchiveFileEntry.IsMember(db, item.Id)) continue;

            _assignments.Add((item.Id, ordered.Select(inference => inference.Infer(db, item)).FirstOrDefault(found => !found.IsUnknown) ?? Destination.Unknown));
        }
        return Task.CompletedTask;
    }

    public void Migrate(ITransaction tx, IDb db)
    {
        foreach (var (item, destination) in _assignments) LibraryDestinations.Write(tx, item, destination);
        logger.LogDebug("Assigned a destination to {Count} library items", _assignments.Count);
    }
}
