using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Library;

/// <summary>
/// <see cref="INexusModsInstaller"/> over the Nexus Mods GraphQL client and the library.
/// </summary>
internal sealed class NexusModsInstaller(
    IConnection connection,
    IEnumerable<IGameData> games,
    IGraphQlClient graphQl,
    INexusModsBrowser browser,
    ILibraryService library,
    ICollectionOwnership ownership,
    TemporaryFileManager temporaryFiles,
    IGameDomainToGameIdMappingCache domains) : INexusModsInstaller
{
    public NexusModsGameId? GameOf(LoadoutId loadout, out string? sharedWith)
    {
        sharedWith = null;
        var db = connection.Db;
        var nexusId = db.NexusIdOfLoadout(loadout, games);
        if (nexusId is not { } id) return null;

        var own = GameInstallMetadata.Game.TryGetValue(Loadout.Load(db, loadout).Installation, out var gameId) ? gameId : (GameId?)null;
        sharedWith = games
            .FirstOrDefault(other => other.GameId != own && other.NexusModsGameId.HasValue && other.NexusModsGameId.Value == id)
            ?.DisplayName;
        return id;
    }

    public async Task<IReadOnlyList<NexusModsFileRow>> Files(NexusModsGameId game, ModId mod, CancellationToken token)
    {
        var result = await graphQl.QueryModFiles(mod, game, token);
        if (result.TryGetError(out NotFound? _)) return [];
        if (!result.TryGetData(out var files))
            throw new InvalidOperationException($"Nexus Mods did not answer for the files of mod {mod}: {string.Join("; ", result.Errors.Select(e => e.Value.Message))}");

        return files.Select(NexusModsFileRows.From).ToArray();
    }

    public async Task<ModId> ModOf(NexusModsGameId game, FileId file, CancellationToken token)
    {
        var result = await graphQl.QueryModFile(file, game, token);
        if (result.TryGetError(out NotFound? _))
            throw new InvalidOperationException($"Nexus Mods has no file {file} for this game.");
        if (!result.TryGetData(out var found))
            throw new InvalidOperationException($"Nexus Mods did not answer for file {file}: {string.Join("; ", result.Errors.Select(e => e.Value.Message))}");

        return ModId.From((uint)found.ModId);
    }

    public LibraryItem.ReadOnly? FindInLibrary(NexusModsGameId game, ModId mod, FileId file, LoadoutId loadout)
    {
        var db = connection.Db;
        LibraryItem.ReadOnly? first = null;
        foreach (var metadata in NexusModsFileMetadata.FindByUid(db, new FileUid(file, game)))
        {
            if (metadata.ModPage.Uid.ModId != mod) continue;
            foreach (var found in NexusModsLibraryItem.FindByFileMetadata(db, metadata))
            {
                var item = found.AsLibraryItem();
                if (IsLinkedIntoLoadout(item, loadout)) return item;
                first ??= item;
            }
        }

        return first;
    }

    private bool IsLinkedIntoLoadout(LibraryItem.ReadOnly item, LoadoutId loadout)
        => LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, item)
            .Any(link => link.AsLoadoutItem().LoadoutId == loadout && !ownership.OwnsLink(connection.Db, link));

    public ResolvedInstall Resolve(NexusModsGameId game, ModId mod, FileId file, string name, bool dependency, LoadoutId loadout)
        => new(name, dependency, FindInLibrary(game, mod, file, loadout), token => Download(game, mod, file, loadout, token));

    private async Task<LibraryItem.ReadOnly> Download(NexusModsGameId game, ModId mod, FileId file, LoadoutId loadout, CancellationToken token)
    {
        // The temporary file has to outlive AddDownload, which moves the finished file into the library's store.
        await using var destination = temporaryFiles.CreateFile();
        var job = await browser.CreateDownloadJob(game, mod, file, destination, token);
        var downloaded = await library.AddDownload(job, new LoadoutAcquisition(loadout));
        return downloaded.AsLibraryItem();
    }

    public Uri DownloadPage(NexusModsGameId game, ModId mod, FileId file)
        => NexusModsUrlBuilder.GetFileDownloadUri(domains.GetDomain(game), mod, file, useNxmLink: true);
}
