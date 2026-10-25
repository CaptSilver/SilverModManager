using JetBrains.Annotations;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;

/// <summary>Answers the save step's questions with the app's Nexus Mods clients.</summary>
[UsedImplicitly]
public sealed class NexusCatalogAdapter(IGraphQlClient graphQl, INexusModsBrowser browser) : INexusCatalog
{
    private const int SearchPageSize = 20;

    public async Task<IReadOnlyList<NexusFile>?> ListFilesAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken)
    {
        var result = await graphQl.QueryModFiles(ModId.From(modId), game, cancellationToken);
        if (!result.TryGetData(out var files)) return null;

        return files
            .Select(f => new NexusFile(FileUid.FromV2Api(f.Uid).FileId.Value, f.Name, f.Version, f.Date))
            .ToArray();
    }

    public async Task<uint?> ModOfFileAsync(NexusModsGameId game, uint fileId, CancellationToken cancellationToken)
    {
        var result = await graphQl.QueryModFile(FileId.From(fileId), game, cancellationToken);
        return result.TryGetData(out var file) ? (uint)file.ModId : null;
    }

    public async Task<IReadOnlyList<NexusModHit>> FindModsByNameAsync(NexusModsGameId game, string name, CancellationToken cancellationToken)
    {
        // Adult mods are included: the name has to match exactly, and the user sees everything that is picked.
        var page = await browser.Search(new NexusModsSearchQuery(game, name, NexusModsSort.Downloads, 0, SearchPageSize, IncludeAdult: true), cancellationToken);
        return page.Hits.Select(h => new NexusModHit(h.ModId.Value, h.Name)).ToArray();
    }

    public async Task<uint?> FindMainFileAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken)
        => await browser.FindMainFile(game, ModId.From(modId), cancellationToken) is { } file ? file.Value : null;
}
