using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace SilverModManager.Library.ModIo;

/// <summary>
/// What every route that brings a mod.io file into the library does with it: check what mod.io lists, check the bytes,
/// then record the file with where it came from.
/// </summary>
internal static class ModIoFiles
{
    /// <summary>
    /// Refuses a listing that cannot be trusted before anything is fetched: a name that could name a path, and a file
    /// with neither a checksum mod.io lists nor a pinned digest.
    /// </summary>
    public static Md5Value? CheckListing(ModIoFile file, Sha256Value? pinned)
    {
        // The name becomes a file name in the library, so it must not be able to name a path.
        if (file.FileName.Length == 0 || file.FileName != Path.GetFileName(file.FileName))
            throw new ModIoIntegrityException($"File name {MarkdownText.Code(file.FileName)} of mod.io file {file.Id} is not a plain file name.");

        var listed = TryParseMd5(file.FileHash?.Md5);
        if (listed is null && pinned is null)
            throw new ModIoIntegrityException($"mod.io lists no usable checksum for {MarkdownText.Code(file.FileName)}, so the download cannot be verified.");
        return listed;
    }

    /// <summary>
    /// Checks <paramref name="path"/> against the listed MD5 and the pinned SHA-256 and adds it to the library. The file
    /// at <paramref name="path"/> is left where it is.
    /// </summary>
    public static async Task<ModIoLibraryFile.ReadOnly> VerifyAndAdd(
        IServiceProvider provider,
        AbsolutePath path,
        ModIoFile file,
        uint gameId,
        uint modId,
        long dateUpdated,
        Sha256Value? pinned,
        IAcquisitionContext acquisition,
        CancellationToken cancellationToken)
    {
        var listedMd5 = CheckListing(file, pinned);
        if (listedMd5 is { } expectedMd5)
        {
            var actualMd5 = await FileHashing.Md5(path, cancellationToken);
            if (!expectedMd5.Equals(actualMd5))
                throw new ModIoIntegrityException($"MD5 mismatch for {MarkdownText.Code(file.FileName)} from mod.io: it lists {expectedMd5.ToString().ToLowerInvariant()}, the download has {actualMd5.ToString().ToLowerInvariant()}.");
        }

        var actual = await FileHashing.Sha256(path, cancellationToken);
        if (pinned is { } expected && !expected.Equals(actual))
            throw new ModIoIntegrityException($"SHA-256 mismatch for {MarkdownText.Code(file.FileName)} from mod.io: expected {expected} (the pinned digest), got {actual}.");

        using var tx = provider.GetRequiredService<IConnection>().BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(provider, tx, path, acquisition);
        var model = new ModIoLibraryFile.New(tx, libraryFile.Id)
        {
            LibraryFile = libraryFile,
            GameId = gameId,
            ModId = modId,
            FileId = file.Id,
            Version = file.Version ?? "",
            DateUpdated = dateUpdated,
        };

        var result = await tx.Commit();
        return result.Remap(model);
    }

    /// <summary>
    /// Whether the exception is mod.io's download address refusing the request: the address expired, or the game only
    /// serves downloads that its own site starts.
    /// </summary>
    public static bool IsRejectedAddress(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone }) return true;
        }

        return false;
    }

    /// <summary>"updated on 2024-03-09", the date in UTC; all some modfiles say.</summary>
    public static string UpdatedOn(long stamp) => $"updated on {DateTimeOffset.FromUnixTimeSeconds(stamp):yyyy-MM-dd}";

    /// <summary>The version a modfile says it is, or when the mod was updated when it says none.</summary>
    public static string Newest(ModIoMod mod, ModIoFile modfile)
        => string.IsNullOrWhiteSpace(modfile.Version) ? UpdatedOn(mod.DateUpdated) : modfile.Version;

    /// <summary>The version the file was downloaded at.</summary>
    public static string Held(ModIoLibraryFile.ReadOnly file)
        => !string.IsNullOrWhiteSpace(file.Version) ? file.Version : file.DateUpdated > 0 ? UpdatedOn(file.DateUpdated) : "";

    // mod.io lists the checksum as 32 hex digits; anything else is as good as none.
    private static Md5Value? TryParseMd5(string? text)
    {
        if (text is null || text.Length != 32 || !text.All(char.IsAsciiHexDigit)) return null;
        return Md5Value.FromHex(text);
    }
}
