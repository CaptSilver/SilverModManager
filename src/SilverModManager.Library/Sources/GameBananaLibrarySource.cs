using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.GameBanana;
using SilverModManager.Library.Lock;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Sources;

/// <summary>
/// Files downloaded from GameBanana, fetched again through <see cref="AddGameBananaFileJob"/> by the site's stable
/// <c>/dl/{fileId}</c> address and checked against the pinned SHA-256. Services are resolved when first used so a host
/// without GameBanana support can still list, export and verify a lock.
/// </summary>
/// <remarks>
/// The lock names the file but not the mod page. A file restored where the library holds no other copy of it records no
/// mod page, so it has no page link and is never checked for updates.
/// </remarks>
internal sealed class GameBananaLibrarySource(IServiceProvider provider, LibraryFileDigests digests) : ILibrarySource
{
    public string Name => LockSources.GameBanana;

    public async ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        if (!file.TryGetAsGameBananaLibraryFile(out var banana)) return null;

        var asset = file.FileName.ToString();
        var sha256 = await digests.EnsureSha256(file, cancellationToken)
            ?? throw new LockException($"{asset} came from GameBanana but the library holds no SHA-256 for it, so it cannot be pinned.");
        return new LibrarySourceEntry(LockExport.SanitizeId(groupName, asset), GameBananaVersions.Held(banana), banana.FileId.ToString(CultureInfo.InvariantCulture), asset, sha256.ToString(), Name);
    }

    public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context)
        => TryParseFileId(entry, out _) ? null : $"{entry.Id}: the lock names no GameBanana file id for {entry.Asset}.";

    public async ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        if (!TryParseFileId(entry, out var fileId)) throw new InvalidOperationException($"{entry.Id}: the lock names no GameBanana file id for {entry.Asset}.");

        // The mod page is not in the lock; an earlier copy of the same file on this machine knows it.
        var earlier = GameBananaLibraryFile.All(context.Connection.Db).FirstOrDefault(file => file.FileId == fileId);
        var modId = earlier.IsValid() ? earlier.ModId : 0;
        var page = modId > 0 ? new Uri($"https://gamebanana.com/mods/{modId}") : new Uri("https://gamebanana.com/");
        var file = new GameBananaFile(fileId, entry.Asset, DownloadUrl: $"https://gamebanana.com/dl/{fileId}");
        var gameId = context.Game is { } game && GameBananaGame.For(game) is { } support ? support.GameBananaGameId : 0;

        var job = AddGameBananaFileJob.Create(provider, modId, file, page, new LockAcquisition(context), gameId, dateUpdated: 0, version: entry.Version, expectedSha256: expected);
        return (await job).AsLibraryFile();
    }

    private static bool TryParseFileId(LibrarySourceEntry entry, out long fileId)
    {
        fileId = 0;
        return entry.FileId is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out fileId) && fileId > 0;
    }
}
