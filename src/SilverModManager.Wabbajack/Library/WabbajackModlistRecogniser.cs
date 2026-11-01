using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Library;

/// <summary>
/// Marks a <c>.wabbajack</c> file that holds a modlist with a <see cref="WabbajackModlistLibraryFile"/>.
/// </summary>
public sealed class WabbajackModlistRecogniser(ILogger<WabbajackModlistRecogniser> logger, IConnection connection) : ILibraryFileRecogniser
{
    private static readonly Extension WabbajackExtension = new(".wabbajack");
    private static readonly string[] ModlistFileNames = ["modlist", "modlist.json"];

    public async ValueTask<Destination?> Recognise(ITransaction tx, LibraryFile.New file, AbsolutePath? extractedFolder, CancellationToken cancellationToken)
    {
        if (extractedFolder is not { } folder) return null;
        if (file.FileName.Extension != WabbajackExtension) return null;

        var modlistFile = ModlistFileNames
            .Select(name => folder.Combine(name))
            .FirstOrDefault(path => path.FileExists);
        if (modlistFile == default) return null;

        ModList list;
        try
        {
            await using var stream = modlistFile.Read();
            list = ModListReader.Read(stream);
        }
        catch (UnsupportedModlistException e)
        {
            // The file is still a library file; it just cannot be installed.
            logger.LogWarning(e, "'{File}' has a modlist entry this version cannot read, so it is not recognised as a modlist", file.FileName);
            return null;
        }

        var modlist = new WabbajackModlistLibraryFile.New(tx, file.Id)
        {
            LibraryFile = file,
            Name = list.Name,
            Author = list.Author,
            Version = list.Version,
            GameType = list.GameType,
            ArchiveCount = list.Archives.Count,
            ArchiveSize = Size.From((ulong)list.Archives.Sum(a => a.Size)),
            InstallSize = Size.From((ulong)list.Directives.Sum(d => d.Size)),
            IsNsfw = list.IsNsfw,
        };

        // No logger: the file only has a temporary id in this transaction, so the once-per-list warning waits for the committed list.
        var destination = ModlistAcquisition.ForList(file.Id, list.GameType, null);
        foreach (var archive in list.Archives)
        {
            // An archive named by a hash this app does not use (the list format only has xxHash64) cannot be joined to the library.
            if (archive.XxHash64.Kind != HashKind.XxHash64) continue;
            var row = new WabbajackModlistArchive.New(tx)
            {
                ModlistId = modlist,
                Identity = archive.XxHash64,
                Size = Size.From((ulong)archive.Size),
                Name = archive.Name,
            };

            // Another list already had the app fetch this file. It stays out of the Mods tab for as long as any list names it.
            var fetched = WabbajackModlistArchive.FindByIdentity(connection.Db, archive.XxHash64)
                .SelectMany(other => other.FetchedFiles)
                .Select(file => (EntityId?)file.Id)
                .FirstOrDefault();
            if (fetched is { } inherited)
            {
                tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, inherited);
                // The held file is now this list's too, as when the acquirer marks it.
                LibraryDestinations.Add(tx, inherited, destination);
            }
        }

        return destination.IsUnknown ? null : destination;
    }
}
