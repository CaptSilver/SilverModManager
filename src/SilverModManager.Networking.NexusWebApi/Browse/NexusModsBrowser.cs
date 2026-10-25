using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

internal sealed class NexusModsBrowser(
    IGraphQlClient graphQl,
    NexusModsLibrary library,
    ILogger<NexusModsBrowser> logger) : INexusModsBrowser
{
    public async Task<NexusModsSearchPage> Search(NexusModsSearchQuery query, CancellationToken cancellationToken)
    {
        var result = await graphQl.QueryMods(NexusModsSearchInputs.Filter(query), NexusModsSearchInputs.Sort(query), query.Offset, query.Count, cancellationToken);
        if (!result.TryGetData(out var mods))
            throw new InvalidOperationException($"Nexus Mods returned no mods for game {query.Game}: {string.Join("; ", result.Errors.Select(e => e.Value.Message))}");

        return new NexusModsSearchPage(mods.Nodes.Select(NexusModsSearchHit.From).ToArray(), query.Offset, mods.TotalCount);
    }

    public async Task<FileId?> FindMainFile(NexusModsGameId game, ModId modId, CancellationToken cancellationToken)
    {
        var result = await graphQl.QueryModFileCategories(modId, game, cancellationToken);
        if (!result.TryGetData(out var files))
        {
            logger.LogInformation("No files found for mod {Mod} of game {Game}", modId, game);
            return null;
        }

        return NexusModsMainFile.Pick(files.Select(file => new NexusModsFileSummary(FileId.From((uint)file.FileId), file.Category)));
    }

    public Task<IJobTask<NexusModsDownloadJob, AbsolutePath>> CreateDownloadJob(NexusModsGameId game, ModId modId, FileId fileId, AbsolutePath destination, CancellationToken cancellationToken)
        => library.CreateDownloadJob(destination, game, modId, fileId, cancellationToken: cancellationToken);
}
