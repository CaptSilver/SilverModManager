using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.Lock;
using SilverModManager.Library.ModIo;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Sources;

/// <summary>
/// Files downloaded from mod.io, fetched again through <see cref="AddModIoFileJob"/> and checked against the pinned
/// SHA-256. Services are resolved when first used so a host without mod.io support can still list, export and verify a
/// lock.
/// </summary>
/// <remarks>
/// The entry's file id is <c>{mod id}:{modfile id}</c>: mod.io only serves a modfile through its mod, and the game comes
/// from the loadout the lock is imported into. The download address mod.io lists expires, so the modfile is listed
/// again at import time.
/// </remarks>
internal sealed class ModIoLibrarySource(IServiceProvider provider, LibraryFileDigests digests) : ILibrarySource
{
    public string Name => LockSources.ModIo;

    public async ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        if (!file.TryGetAsModIoLibraryFile(out var modio)) return null;

        var asset = file.FileName.ToString();
        var sha256 = await digests.EnsureSha256(file, cancellationToken)
            ?? throw new LockException($"{asset} came from mod.io but the library holds no SHA-256 for it, so it cannot be pinned.");
        var ids = string.Create(CultureInfo.InvariantCulture, $"{modio.ModId}:{modio.FileId}");
        return new LibrarySourceEntry(LockExport.SanitizeId(groupName, asset), ModIoFiles.Held(modio), ids, asset, sha256.ToString(), Name);
    }

    public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context)
    {
        if (!TryParseIds(entry, out _, out _)) return $"{entry.Id}: the lock names no mod and file id for {entry.Asset}.";
        return context.Game is { } game && ModIoGame.For(game) is not null ? null : $"{entry.Id}: {entry.Asset} is a mod.io file and the loadout's game has no mods on mod.io.";
    }

    public async ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        if (!TryParseIds(entry, out var modId, out var fileId)) throw new InvalidOperationException($"{entry.Id}: the lock names no mod and file id for {entry.Asset}.");
        var support = context.Game is { } game ? ModIoGame.For(game) : null;
        if (support is null) throw new InvalidOperationException($"{entry.Id}: the loadout's game has no mods on mod.io.");

        // The address listed with a file expires, so the modfile is read now rather than carried in the lock.
        var files = await provider.GetRequiredService<IModIoApi>().Files(support.ModIoGameId, modId, cancellationToken);
        var listed = files.FirstOrDefault(candidate => candidate.Id == fileId)
            ?? throw new LockException($"{entry.Id}: mod.io no longer lists file {fileId} of mod {modId}, so {entry.Asset} cannot be restored.");

        var page = new Uri($"https://mod.io/g/{support.ModIoNameId}");
        var job = AddModIoFileJob.Create(provider, support.ModIoGameId, modId, listed, page, new LockAcquisition(context), dateUpdated: 0, expectedSha256: expected);
        return (await job).AsLibraryFile();
    }

    private static bool TryParseIds(LibrarySourceEntry entry, out uint modId, out uint fileId)
    {
        modId = fileId = 0;
        var parts = entry.FileId?.Split(':');
        return parts is [var mod, var file]
            && uint.TryParse(mod, NumberStyles.None, CultureInfo.InvariantCulture, out modId) && modId > 0
            && uint.TryParse(file, NumberStyles.None, CultureInfo.InvariantCulture, out fileId) && fileId > 0;
    }
}
