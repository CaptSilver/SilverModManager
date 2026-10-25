using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// What the Nexus Mods browse page needs from the site: a game's catalog, a mod's main file and the download of a
/// file for the library.
/// </summary>
public interface INexusModsBrowser
{
    /// <summary>Fetches one page of the catalog. Throws when the API answers without data.</summary>
    Task<NexusModsSearchPage> Search(NexusModsSearchQuery query, CancellationToken cancellationToken);

    /// <summary>The mod's one MAIN-category file, or null when there is none, there are several, or the mod is unknown.</summary>
    Task<FileId?> FindMainFile(NexusModsGameId game, ModId modId, CancellationToken cancellationToken);

    /// <summary>
    /// A job that downloads the file through the premium download API into <paramref name="destination"/>, the same way
    /// a collection download does. Handing it to the library service adds the finished file to the library.
    /// </summary>
    Task<IJobTask<NexusModsDownloadJob, AbsolutePath>> CreateDownloadJob(NexusModsGameId game, ModId modId, FileId fileId, AbsolutePath destination, CancellationToken cancellationToken);
}
