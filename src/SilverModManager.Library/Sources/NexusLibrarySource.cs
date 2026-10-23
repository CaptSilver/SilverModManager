using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Library.Lock;
using SilverModManager.Networking.NexusWebApi;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Library.Sources;

/// <summary>
/// Nexus Mods files, fetched by file id like collection downloads do. Services are resolved when first used so a host
/// without Nexus Mods support can still list, export and verify a lock.
/// </summary>
internal sealed class NexusLibrarySource(IServiceProvider provider, LibraryFileDigests digests) : ILibrarySource
{
    public string Name => LockSources.Nexus;

    public async ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        if (!item.TryGetAsNexusModsLibraryItem(out var nexus)) return null;

        var asset = file.FileName.ToString();
        var sha256 = await digests.EnsureSha256(file, cancellationToken)
            ?? throw new LockException($"'{asset}' has no SHA-256 and its bytes are not in the file store, so it cannot be pinned.");

        var id = LockExport.SanitizeId(nexus.ModPageMetadata.Name, asset);
        return new LibrarySourceEntry(id, nexus.FileMetadata.Version, nexus.FileMetadata.Uid.FileId.Value.ToString(), asset, sha256.ToString(), Name);
    }

    public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context)
    {
        if (context.NexusGame is null)
            return $"{entry.Id}: the loadout's game has no Nexus Mods id, so file {entry.FileId} cannot be looked up.";
        if (!uint.TryParse(entry.FileId, out _))
            return $"{entry.Id}: the lock has no usable Nexus Mods file id ('{entry.FileId}').";
        return null;
    }

    public async ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        if (context.NexusGame is not { } game || !uint.TryParse(entry.FileId, out var fileNumber))
            throw new InvalidOperationException($"{entry.Id}: {entry.Asset} cannot be downloaded from Nexus Mods without a Nexus Mods game and a file id.");

        var nexusModsLibrary = provider.GetRequiredService<NexusModsLibrary>();
        await using var destination = provider.GetRequiredService<TemporaryFileManager>().CreateFile();

        var job = await nexusModsLibrary.CreateDownloadJob(destination, game, FileId.From(fileNumber), cancellationToken: cancellationToken);
        return await provider.GetRequiredService<ILibraryService>().AddDownload(job);
    }
}
