using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;

/// <summary>
/// Puts a Nexus Mods file in the library the way the other pages do: a premium account downloads it through the
/// same job an <c>nxm</c> link uses, any other account gets the file's page opened, where the site hands out the key
/// the download needs.
/// </summary>
[UsedImplicitly]
public sealed class NexusModDownloader(IServiceProvider provider) : IModDownloader
{
    public async Task<ModDownloadResult> DownloadAsync(NexusModsGameId game, ModToSave mod, CancellationToken cancellationToken)
    {
        var modId = ModId.From(mod.ModId);
        var fileId = FileId.From(mod.FileId);

        if (!provider.GetRequiredService<ILoginManager>().IsPremium)
        {
            var domain = provider.GetRequiredService<IGameDomainToGameIdMappingCache>().GetDomain(game);
            provider.GetRequiredService<IOSInterop>().OpenUri(NexusModsUrlBuilder.GetFileDownloadUri(domain, modId, fileId, useNxmLink: true));
            return ModDownloadResult.OpenedInBrowser;
        }

        // The temporary file has to outlive AddDownload, which moves the finished file into the library's store.
        await using var destination = provider.GetRequiredService<TemporaryFileManager>().CreateFile();
        var job = await provider.GetRequiredService<NexusModsLibrary>().CreateDownloadJob(destination, game, modId, fileId, cancellationToken: cancellationToken);
        await provider.GetRequiredService<ILibraryService>().AddDownload(job);
        return ModDownloadResult.Added;
    }
}
