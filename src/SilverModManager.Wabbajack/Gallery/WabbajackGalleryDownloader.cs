using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Downloads a gallery list into the library. The file is checked against the size and xxHash64 the gallery gives
/// before the library sees it, then added the way a <c>.wabbajack</c> chosen from disk is, so the result is the
/// same library item the modlist page opens.
/// </summary>
public sealed class WabbajackGalleryDownloader(
    IModlistDownloadJobs jobs,
    ILibraryService libraryService,
    IConnection connection,
    TemporaryFileManager temporaryFileManager)
{
    /// <exception cref="GalleryDownloadException">The entry can't be verified, the download failed, or the bytes aren't what the gallery described.</exception>
    public async Task<WabbajackModlistLibraryFile.ReadOnly> Download(GalleryEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry is not { Download: { } url, DownloadHash: { } expectedHash, DownloadSize: { } expectedSize })
            throw new GalleryDownloadException($"'{entry.Title}' has no download link, hash and size to check it against, so it is not downloaded");

        await using var folder = temporaryFileManager.CreateFolder();
        var file = folder.Path.Combine(FileNameFor(entry));

        try
        {
            try
            {
                await jobs.Create(url, file);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new GalleryDownloadException($"Downloading '{entry.Title}' failed: {e.Message}", e);
            }

            await Verify(entry, file, expectedHash, expectedSize, cancellationToken);

            var added = await libraryService.AddLocalFile(file);
            var libraryFile = added.AsLibraryFile();
            if (!libraryFile.TryGetAsWabbajackModlistLibraryFile(out var modlist))
            {
                await libraryService.RemoveLibraryItems([libraryFile.AsLibraryItem()]);
                throw new GalleryDownloadException($"'{entry.Title}' downloaded correctly but is not a Wabbajack modlist");
            }

            using var tx = connection.BeginTransaction();
            tx.Add(modlist.Id, WabbajackModlistLibraryFile.GalleryDownloadUrl, url.ToString());
            var result = await tx.Commit();
            return WabbajackModlistLibraryFile.Load(result.Db, modlist.Id);
        }
        finally
        {
            // The folder goes with the dispose; this also covers a file left over by a failed one.
            if (file.FileExists) file.Delete();
        }
    }

    private static async Task Verify(GalleryEntry entry, AbsolutePath file, Hash expectedHash, ulong expectedSize, CancellationToken cancellationToken)
    {
        if (!file.FileExists) throw new GalleryDownloadException($"Downloading '{entry.Title}' produced no file");

        var size = file.FileInfo.Size.Value;
        if (size != expectedSize)
        {
            file.Delete();
            throw new GalleryDownloadException($"'{entry.Title}' is {size} bytes but the gallery says its size is {expectedSize}; the file was deleted");
        }

        Hash actual;
        await using (var stream = file.Read())
            actual = await Xx64Hasher.HashAsync(stream, cancellationToken: cancellationToken);
        if (actual != expectedHash)
        {
            file.Delete();
            throw new GalleryDownloadException(
                $"'{entry.Title}' has hash {WabbajackHash.ToBase64(actual)} but the gallery says {WabbajackHash.ToBase64(expectedHash)}; the file was deleted");
        }
    }

    // The title is untrusted and becomes a file name, so it is reduced to plain characters.
    internal static string FileNameFor(GalleryEntry entry)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var name = new string(entry.Title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        return $"{(name.Length == 0 ? "modlist" : name)}.wabbajack";
    }
}
