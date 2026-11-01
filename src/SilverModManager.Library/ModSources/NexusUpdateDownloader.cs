using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Fetches the newer file of a Nexus Mods update into the library. A seam of its own so the source's decisions
/// (what to download, how many at once, what to replace) are testable without the Nexus Mods API.
/// </summary>
public interface INexusUpdateDownloader
{
    /// <summary>
    /// Downloads <paramref name="newest"/> and adds it to the library as the successor of the library items that
    /// hold <paramref name="replaced"/>.
    /// </summary>
    ValueTask<LibraryFile.ReadOnly> Download(NexusModsFileMetadata.ReadOnly newest, IReadOnlyList<NexusModsFileMetadata.ReadOnly> replaced, CancellationToken cancellationToken);
}

internal sealed class NexusUpdateDownloader(NexusModsLibrary nexusModsLibrary, ILibraryService libraryService, IConnection connection, TemporaryFileManager temporaryFileManager) : INexusUpdateDownloader
{
    public async ValueTask<LibraryFile.ReadOnly> Download(NexusModsFileMetadata.ReadOnly newest, IReadOnlyList<NexusModsFileMetadata.ReadOnly> replaced, CancellationToken cancellationToken)
    {
        await using var tempPath = temporaryFileManager.CreateFile();
        var job = await nexusModsLibrary.CreateDownloadJob(tempPath, newest, cancellationToken: cancellationToken);
        return await AddUpdatedFile(job, replaced);
    }

    /// <summary>
    /// Adds the newer file of an update. It is for the games of the files it supersedes, not for the game of the page
    /// that started the update: a Nexus Mods page serves every game that shares its id, so an update of a file that is
    /// only for one of them must stay only for that one.
    /// </summary>
    internal async Task<LibraryFile.ReadOnly> AddUpdatedFile(IJobTask<IDownloadJob, AbsolutePath> job, IEnumerable<NexusModsFileMetadata.ReadOnly> replacedFiles)
    {
        var replaced = replacedFiles
            .SelectMany(file => NexusModsLibraryItem.FindByFileMetadata(connection.Db, file))
            .Select(item => item.AsLibraryItem().LibraryItemId)
            .ToArray();
        return await libraryService.AddDownload(job, new UpdateAcquisition(replaced));
    }
}
