using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// An update of a library file is for the games the old file was for, not for the game of the page that started the
/// update: a Nexus Mods page is shared by every game with its id. The tests call the helper both update paths share;
/// that the paths call it is not covered, since they download through the real Nexus Mods library.
/// </summary>
public class NexusUpdateDownloaderTests(IServiceProvider provider, IConnection connection)
{
    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 74000;

    private static readonly GameId SiblingGame = GameId.From("UpdateTestSiblingGame");
    private static readonly GameId OtherSiblingGame = GameId.From("UpdateTestOtherSibling");

    private sealed class CompletedDownloadJob(AbsolutePath path) : IDownloadJob, IJobDefinitionWithStart<CompletedDownloadJob, AbsolutePath>
    {
        public AbsolutePath Destination => path;
        public ValueTask<AbsolutePath> StartAsync(IJobContext<CompletedDownloadJob> context) => ValueTask.FromResult(path);
        public ValueTask AddMetadata(ITransaction tx, LibraryFile.New libraryFile) => ValueTask.CompletedTask;
    }

    /// <summary>A Nexus Mods library item for a file, with the games it is for.</summary>
    private async Task<NexusModsFileMetadata.ReadOnly> SeedNexusItem(Destination destination)
    {
        var number = _nextId++;
        using var tx = connection.BeginTransaction();
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(number), NexusModsGameId.From(1704)),
            Name = "Shared by SE and VR",
            GameDomain = GameDomain.From("skyrimspecialedition"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(number), NexusModsGameId.From(1704)),
            Name = "old.zip",
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        var item = new NexusModsLibraryItem.New(tx, out var id)
        {
            FileMetadataId = metadata.Id,
            ModPageMetadataId = page.Id,
            LibraryItem = new LibraryItem.New(tx, id) { Name = "old.zip" },
        };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return NexusModsFileMetadata.Load(result.Db, result[metadata.Id]);
    }

    private NexusUpdateDownloader Downloader() => new(
        nexusModsLibrary: null!, // only AddUpdatedFile is called, which never reaches the Nexus Mods API
        provider.GetRequiredService<ILibraryService>(),
        connection,
        provider.GetRequiredService<TemporaryFileManager>());

    private IJobTask<IDownloadJob, AbsolutePath> CompletedJob(DownloadedFile download)
        => provider.GetRequiredService<IJobMonitor>().Begin<CompletedDownloadJob, AbsolutePath>(new CompletedDownloadJob(download.Path));

    [Fact]
    public async Task AddUpdatedFile_KeepsTheOldFilesGames_NotThePagesGame()
    {
        var oldFile = await SeedNexusItem(new Destination(new HashSet<GameId> { SiblingGame, OtherSiblingGame }, Shared: true));
        using var download = new DownloadedFile();

        var added = await Downloader().AddUpdatedFile(CompletedJob(download), [oldFile]);

        var item = LibraryItem.Load(connection.Db, added.Id);
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([SiblingGame, OtherSiblingGame]);
        item.Contains(LibraryItem.DestinationsShared).Should().BeTrue();
    }

    [Fact]
    public async Task AddUpdatedFile_OfAnUnassignedOldFile_IsUnassigned()
    {
        var oldFile = await SeedNexusItem(Destination.Unknown);
        using var download = new DownloadedFile();

        var added = await Downloader().AddUpdatedFile(CompletedJob(download), [oldFile]);

        var item = LibraryItem.Load(connection.Db, added.Id);
        LibraryDestinations.GamesOf(item).Should().BeEmpty();
        item.Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
    }

    /// <summary>A downloaded file in a scratch directory that is removed with it.</summary>
    private sealed class DownloadedFile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("update-destination-").FullName;

        public DownloadedFile() => File.WriteAllText(System.IO.Path.Combine(_directory, "new.zip"), Guid.NewGuid().ToString());

        public AbsolutePath Path => FileSystem.Shared.FromUnsanitizedFullPath(System.IO.Path.Combine(_directory, "new.zip"));

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors()
                .AddLibraryServiceForTests();
        }
    }
}
