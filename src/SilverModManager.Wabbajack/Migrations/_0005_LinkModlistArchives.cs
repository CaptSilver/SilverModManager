using Microsoft.Extensions.Logging;
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Migrations;

/// <summary>
/// Gives every modlist that was added before its archives were recorded a <see cref="WabbajackModlistArchive"/> row
/// per archive, and marks the library files that already match as fetched for it.
/// </summary>
/// <remarks>
/// The list is read back from the file store, which cannot be done inside the transaction, so the rows are worked out
/// in <see cref="Prepare"/>. A file the user had before this ran and that matches a list is marked too: hash matching
/// cannot tell it from one the app fetched. It stays visible while a loadout uses it and is hidden from the Mods tab
/// otherwise, until it is installed or the list is removed. A list that cannot be read keeps no rows.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0005_LinkModlistArchives(IFileStore fileStore, ILogger<_0005_LinkModlistArchives> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0005_LinkModlistArchives));

    private readonly List<(EntityId List, IReadOnlyList<ArchiveEntry> Archives)> _lists = [];

    public async Task Prepare(IDb db)
    {
        _lists.Clear();
        foreach (var datom in db.Datoms(WabbajackModlistLibraryFile.Name))
        {
            var list = WabbajackModlistLibraryFile.Load(db, datom.E);
            if (!list.IsValid() || WabbajackModlistArchive.FindByModlist(db, list.Id).Any()) continue;
            if (!list.AsLibraryFile().TryGetAsLibraryArchive(out var archive))
            {
                logger.LogWarning("The modlist '{Name}' is not an archive, so its downloads cannot be recorded", list.Name);
                continue;
            }

            try
            {
                var read = await ModlistArchive.ReadModlist(fileStore, archive);
                _lists.Add((list.Id, read.Archives));
            }
            catch (Exception e) when (e is UnsupportedModlistException or IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                logger.LogWarning(e, "Could not read the modlist '{Name}', so its downloads are not recorded", list.Name);
            }
        }
    }

    public void Migrate(ITransaction tx, IDb db)
    {
        var locator = new LibraryArchiveLocator();
        foreach (var (list, archives) in _lists)
        {
            foreach (var archive in archives)
            {
                if (archive.XxHash64.Kind != HashKind.XxHash64) continue;
                var row = new WabbajackModlistArchive.New(tx)
                {
                    ModlistId = list,
                    Identity = archive.XxHash64,
                    Size = Size.From((ulong)archive.Size),
                    Name = archive.Name,
                };
                if (locator.FindByXxHash64(db, archive.XxHash64) is { } held) tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, held.Id);
            }
        }

        logger.LogDebug("Recorded the archives of {Count} modlists", _lists.Count);
    }
}
