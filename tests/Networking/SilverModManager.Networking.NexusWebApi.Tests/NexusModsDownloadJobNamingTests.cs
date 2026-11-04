using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.HttpDownloader.Tests;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// A Nexus Mods download lands in a temporary file, so the library row must be given the archive's real name
/// rather than keep the temporary one.
/// </summary>
public class NexusModsDownloadJobNamingTests(IServiceProvider serviceProvider, ILibraryService libraryService, TemporaryFileManager temporaryFileManager)
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(1704);

    private async Task<NexusModsFileMetadata.ReadOnly> SeedFile(uint modNumber, uint fileNumber, string displayName)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        using var tx = connection.BeginTransaction();
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(modNumber), Game),
            Name = "A mod",
            GameDomain = GameDomain.From("skyrimspecialedition"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var file = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(fileNumber), Game),
            Name = displayName,
            Version = "1.0",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        var result = await tx.Commit();
        return result.Remap(file);
    }

    private async Task<LibraryFile.ReadOnly> Download(NexusModsFileMetadata.ReadOnly metadata, Uri uri)
    {
        await using var destination = temporaryFileManager.CreateFile();
        var httpJob = HttpDownloadJob.Create(serviceProvider, uri, new Uri("https://www.nexusmods.com/skyrimspecialedition/mods/1"), destination.Path);
        var nexusJob = NexusModsDownloadJob.Create(serviceProvider, httpJob, metadata);
        return await libraryService.AddDownload(nexusJob, UnassignedAcquisition.Instance);
    }

    [Fact]
    public async Task ADownloadIsNamedAfterTheArchiveTheLinkPointsAt_NotAfterTheTemporaryFile()
    {
        using var server = new SingleFileServer("archive bytes"u8.ToArray());
        var metadata = await SeedFile(modNumber: 9001, fileNumber: 9002, displayName: "Main File");
        var link = new Uri(server.Uri, "/cdn/1704/9001/Real%20Mod-9001-1-0-1700000000.zip?md5=abc&expires=1");

        var libraryFile = await Download(metadata, link);

        libraryFile.FileName.ToString().Should().Be("Real Mod-9001-1-0-1700000000.zip");
        libraryFile.FileName.ToString().Should().NotEndWith(".tmp");
        libraryFile.AsLibraryItem().Name.Should().Be("Main File", "the library lists a Nexus file by the name of its file on the page");
    }

    [Fact]
    public async Task ADownloadWhoseLinkHasNoFileName_IsNamedAfterTheFileOnThePage()
    {
        using var server = new SingleFileServer("archive bytes"u8.ToArray());
        var metadata = await SeedFile(modNumber: 9011, fileNumber: 9012, displayName: "Optional File");

        var libraryFile = await Download(metadata, new Uri(server.Uri, "/?token=x"));

        libraryFile.FileName.ToString().Should().Be("Optional File");
        libraryFile.AsLibraryItem().Name.Should().Be("Optional File");
    }
}
