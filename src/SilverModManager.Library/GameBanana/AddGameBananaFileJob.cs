using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Library.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

/// <summary>
/// Downloads one file of a GameBanana mod, checks it and adds it to the library together with where it came from. A
/// file whose bytes do not match is never added.
/// </summary>
/// <remarks>
/// GameBanana lists an MD5 for every file on the mod's page, so the bytes must match it. A file fetched without the
/// page (a lock names the file id and a SHA-256 only) must match the pinned SHA-256 instead. The library then holds
/// both digests of the file.
/// </remarks>
[PublicAPI]
public class AddGameBananaFileJob : IJobDefinitionWithStart<AddGameBananaFileJob, GameBananaLibraryFile.ReadOnly>
{
    /// <summary>The mod page's id, or zero when the file was fetched by its id alone.</summary>
    public required long ModId { get; init; }

    /// <summary>The file as the mod page lists it.</summary>
    public required GameBananaFile File { get; init; }

    /// <summary>The mod's page, which the download names as where it came from.</summary>
    public required Uri Page { get; init; }

    /// <summary>Where the downloaded file goes: the games it is recorded for.</summary>
    public required IAcquisitionContext Acquisition { get; init; }

    /// <summary>GameBanana's id for the game the mod is listed under.</summary>
    public int GameId { get; init; }

    /// <summary>The page's update stamp now, which the update check compares against later.</summary>
    public long DateUpdated { get; init; }

    /// <summary>The version text of the page, null when it has none.</summary>
    public string? Version { get; init; }

    /// <summary>The digest the downloaded bytes must have, if one is already pinned.</summary>
    public Sha256Value? ExpectedSha256 { get; init; }

    private HttpClient Client { get; }
    private IServiceProvider ServiceProvider { get; }

    private AddGameBananaFileJob(IServiceProvider serviceProvider, HttpClient client)
    {
        ServiceProvider = serviceProvider;
        Client = client;
    }

    public static IJobTask<AddGameBananaFileJob, GameBananaLibraryFile.ReadOnly> Create(
        IServiceProvider provider,
        long modId,
        GameBananaFile file,
        Uri page,
        IAcquisitionContext acquisition,
        int gameId,
        long dateUpdated,
        string? version,
        Sha256Value? expectedSha256 = null)
        => Create(provider, provider.GetRequiredService<HttpClient>(), modId, file, page, acquisition, gameId, dateUpdated, version, expectedSha256);

    internal static IJobTask<AddGameBananaFileJob, GameBananaLibraryFile.ReadOnly> Create(
        IServiceProvider provider,
        HttpClient client,
        long modId,
        GameBananaFile file,
        Uri page,
        IAcquisitionContext acquisition,
        int gameId,
        long dateUpdated,
        string? version,
        Sha256Value? expectedSha256 = null)
    {
        var job = new AddGameBananaFileJob(provider, client)
        {
            ModId = modId,
            File = file,
            Page = page,
            Acquisition = acquisition,
            GameId = gameId,
            DateUpdated = dateUpdated,
            Version = string.IsNullOrWhiteSpace(version) ? null : version,
            ExpectedSha256 = expectedSha256,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<AddGameBananaFileJob, GameBananaLibraryFile.ReadOnly>(job);
    }

    public async ValueTask<GameBananaLibraryFile.ReadOnly> StartAsync(IJobContext<AddGameBananaFileJob> context)
    {
        // The name becomes a file name in a folder of ours, so it must not be able to name a path. Path.GetFileName
        // passes "." and "..", so those and control characters are refused by hand.
        if (!IsPlainFileName(File.FileName))
            throw new GameBananaIntegrityException($"File name {MarkdownText.Code(File.FileName)} of GameBanana file {File.Id} is not a plain file name.");

        var listedMd5 = TryParseMd5(File.Md5Checksum);
        if (listedMd5 is null && ExpectedSha256 is null)
            throw new GameBananaIntegrityException($"GameBanana lists no usable checksum for {MarkdownText.Code(File.FileName)}, so the download cannot be verified.");

        var temporaryFileManager = ServiceProvider.GetRequiredService<TemporaryFileManager>();
        await using var folder = temporaryFileManager.CreateFolder();
        var downloaded = folder.Path.Combine(File.FileName);
        if (downloaded.Parent != folder.Path)
            throw new GameBananaIntegrityException($"File name {MarkdownText.Code(File.FileName)} of GameBanana file {File.Id} is not a plain file name.");

        try
        {
            await context.YieldAsync();
            var download = new HttpDownloadJob
            {
                Uri = File.Download,
                DownloadPageUri = Page,
                Destination = downloaded,
                Client = Client,
                Logger = ServiceProvider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            };
            var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();
            var downloading = monitor.Begin<HttpDownloadJob, AbsolutePath>(download);
            // The download is a job of its own, so cancelling this one doesn't reach it unless passed on.
            await using (context.CancellationToken.Register(() => monitor.Cancel(downloading)))
            {
                await downloading;
            }

            if (listedMd5 is { } expectedMd5)
            {
                var actualMd5 = await FileHashing.Md5(downloaded, context.CancellationToken);
                if (!expectedMd5.Equals(actualMd5))
                    throw new GameBananaIntegrityException($"MD5 mismatch for {MarkdownText.Code(File.FileName)} from GameBanana: the page lists {expectedMd5.ToString().ToLowerInvariant()}, the download has {actualMd5.ToString().ToLowerInvariant()}.");
            }

            var actual = await FileHashing.Sha256(downloaded, context.CancellationToken);
            if (ExpectedSha256 is { } pinned && !pinned.Equals(actual))
                throw new GameBananaIntegrityException($"SHA-256 mismatch for {MarkdownText.Code(File.FileName)} from GameBanana: expected {pinned} (the pinned digest), got {actual}.");

            await context.YieldAsync();
            using var tx = ServiceProvider.GetRequiredService<IConnection>().BeginTransaction();
            var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, downloaded, Acquisition);

            var model = new GameBananaLibraryFile.New(tx, libraryFile.Id)
            {
                LibraryFile = libraryFile,
                ModId = ModId,
                FileId = File.Id,
                GameId = GameId,
                ListedFileName = File.FileName,
                DateUpdated = DateUpdated,
            };
            if (Version is not null) tx.Add(model.Id, GameBananaLibraryFile.ListedVersion, Version);

            var result = await tx.Commit();
            return result.Remap(model);
        }
        finally
        {
            // The folder is removed on dispose; this also covers a file that outlives a failed dispose.
            if (downloaded.FileExists) downloaded.Delete();
        }
    }

    private static bool IsPlainFileName(string name)
        => name.Length > 0 && name is not ("." or "..") && name == Path.GetFileName(name) && !name.Any(char.IsControl);

    // The page lists the checksum as 32 hex digits; anything else is as good as none.
    private static Md5Value? TryParseMd5(string? text)
    {
        if (text is null || text.Length != 32 || !text.All(char.IsAsciiHexDigit)) return null;
        return Md5Value.FromHex(text);
    }
}
