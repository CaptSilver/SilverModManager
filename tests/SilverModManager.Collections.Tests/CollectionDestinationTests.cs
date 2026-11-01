using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;

namespace SilverModManager.Collections.Tests;

/// <summary>
/// A collection's downloads go to the library for the game of the loadout the collection is for. The network is a
/// fake: the Nexus Mods API answers with links to an in-memory file server, and the account is premium.
/// </summary>
public class CollectionDestinationTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionDestinationTests>(helper)
{
    private const string ExternalUrl = "https://files.test/external.zip";
    private const string NexusUrl = "https://files.test/nexus.zip";
    private const string CollectionUrl = "https://files.test/collection.zip";

    // Field initialisers run before the base constructor builds the services, which is when the fakes are read.
    private readonly InMemoryFiles _files = new();

    private sealed class InMemoryFiles : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _bodies = [];

        public void Serve(string url, byte[] body) => _bodies[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_bodies.TryGetValue(request.RequestUri!.ToString(), out var body)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            response.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class CyberpunkDomains : IGameDomainToGameIdMappingCache
    {
        public GameDomain GetDomain(NexusModsGameId id) => GameDomain.From("cyberpunk2077");
        public NexusModsGameId GetId(GameDomain domain) => OfflineCollections.Game;
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var login = Substitute.For<ILoginManager>();
        login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(new UserInfo
        {
            UserId = SilverModManager.Abstractions.NexusWebApi.Types.UserId.From(1), Name = "premium", UserRole = UserRole.Premium, AvatarUrl = null,
        });
        login.IsPremium.Returns(true);

        var api = Substitute.For<INexusApiClient>();
        api.DownloadLinksAsync("cyberpunk2077", Arg.Any<ModId>(), Arg.Any<FileId>(), Arg.Any<CancellationToken>()).Returns(new Response<DownloadLink[]>
        {
            Data = [new DownloadLink { Name = "fake", _ShortName = "fake", Uri = new Uri(NexusUrl) }],
            Metadata = new ResponseMetadata(),
            StatusCode = HttpStatusCode.OK,
        });
        // Slug and revision number are value objects whose default has no content, which Arg.Any cannot compare.
        api.CollectionDownloadLinksAsync(default, default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(new Response<CollectionDownloadLinks>
            {
                Data = new CollectionDownloadLinks { DownloadLinks = [new CollectionLink { Name = "fake", ShortName = "fake", Uri = new Uri(CollectionUrl) }] },
                Metadata = new ResponseMetadata(),
                StatusCode = HttpStatusCode.OK,
            }));

        // The revision query is the step after the collection file is added; stopping there is enough to see the file.
        var graphQl = Substitute.For<IGraphQlClient>();
        graphQl.QueryCollectionRevision(default, default, default)
            .ReturnsForAnyArgs<ValueTask<GraphQlResult<ICollectionRevision, NotFound>>>(_ => throw new RevisionQueryReached());

        base.AddServices(services);
        services.Replace(ServiceDescriptor.Singleton(login));
        services.Replace(ServiceDescriptor.Singleton(api));
        services.Replace(ServiceDescriptor.Singleton(graphQl));
        services.Replace(ServiceDescriptor.Singleton<IGameDomainToGameIdMappingCache>(new CyberpunkDomains()));
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(_files)));
        return services;
    }

    private sealed class RevisionQueryReached : Exception;

    private static CollectionDownloadNexusMods.ReadOnly NexusDownload(CollectionRevisionMetadata.ReadOnly revision)
    {
        revision.Downloads.Single(d => d.Name == "Nexus mod").TryGetAsCollectionDownloadNexusMods(out var download).Should().BeTrue();
        return download;
    }

    private static CollectionDownloadExternal.ReadOnly ExternalDownload(CollectionRevisionMetadata.ReadOnly revision)
    {
        revision.Downloads.Single(d => d.Name == "External mod").TryGetAsCollectionDownloadExternal(out var download).Should().BeTrue();
        return download;
    }

    private CollectionDownloader Downloader => new(ServiceProvider);

    private async Task<(Mod External, Mod Nexus, byte[] ExternalBytes, byte[] NexusBytes, CollectionRevisionMetadata.ReadOnly Revision)> CreateCollection()
    {
        var offline = new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager);
        var folder = TemporaryFileManager.CreateFolder();
        var external = await offline.CreateFomodMod(folder.Path, 0, plain: true);
        var nexus = await offline.CreateFomodMod(folder.Path, 1, plain: true);
        var externalBytes = await external.Path.ReadAllBytesAsync();
        var nexusBytes = await nexus.Path.ReadAllBytesAsync();
        _files.Serve(ExternalUrl, externalBytes);
        _files.Serve(NexusUrl, nexusBytes);

        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Destination", DomainName = GameDomain.From("cyberpunk2077") },
            Mods =
            [
                new Mod
                {
                    Name = "External mod", DomainName = GameDomain.From("cyberpunk2077"),
                    Source = new ModSource
                    {
                        Type = ModSourceType.Direct, Md5 = external.Md5, Url = new Uri(ExternalUrl),
                        LogicalFilename = "external.zip", FileSize = Size.From((ulong)externalBytes.Length),
                    },
                },
                new Mod
                {
                    Name = "Nexus mod", DomainName = GameDomain.From("cyberpunk2077"),
                    Source = new ModSource { Type = ModSourceType.NexusMods, ModId = ModId.From(77), FileId = FileId.From(78), Md5 = nexus.Md5 },
                },
            ],
        };
        var (_, revision, _) = await offline.Create(manifest);
        return (manifest.Mods[0], manifest.Mods[1], externalBytes, nexusBytes, revision);
    }

    [Fact]
    public async Task Download_NexusModsItem_RecordsTheTargetLoadoutsGame()
    {
        var (_, _, _, _, revision) = await CreateCollection();
        var loadout = await CreateLoadout();
        var download = NexusDownload(revision);

        await Downloader.Download(download, loadout.LoadoutId, CancellationToken.None);

        var item = NexusModsLibraryItem.FindByFileMetadata(Connection.Db, download.FileMetadata).Should().ContainSingle().Subject.AsLibraryItem();
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([GameInstallation.Game.GameId]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public async Task Download_ExternalItem_RecordsTheTargetLoadoutsGame()
    {
        var (_, _, _, _, revision) = await CreateCollection();
        var loadout = await CreateLoadout();
        var download = ExternalDownload(revision);

        await Downloader.Download(download, loadout.LoadoutId, CancellationToken.None);

        var file = DirectDownloadLibraryFile.All(Connection.Db).Single(f => f.LogicalFileName == "External mod");
        var item = file.AsLocalFile().AsLibraryFile().AsLibraryItem();
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([GameInstallation.Game.GameId]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadItems_RecordsTheTargetLoadoutsGame_ForEveryItem()
    {
        var (_, _, _, _, revision) = await CreateCollection();
        var loadout = await CreateLoadout();

        await Downloader.DownloadItems(revision, CollectionDownloader.ItemType.Required, Connection.Db, loadout.LoadoutId);

        var items = LibraryDestinations.ItemsFor(Connection.Db, GameInstallation.Game.GameId).Select(item => item.Id).ToHashSet();
        var external = DirectDownloadLibraryFile.All(Connection.Db).Single(f => f.LogicalFileName == "External mod");
        var nexus = NexusModsLibraryItem.FindByFileMetadata(Connection.Db, NexusDownload(revision).FileMetadata).Single();
        items.Should().Contain([external.Id, nexus.Id]);
    }

    [Fact]
    public async Task GetOrAddRevision_RecordsTheTargetLoadoutsGame()
    {
        var collectionZip = TemporaryFileManager.CreateFile();
        await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager)
            .WriteCollectionArchive(collectionZip.Path, new CollectionRoot
            {
                Info = new CollectionInfo { Name = "Fetched", DomainName = GameDomain.From("cyberpunk2077") },
                Mods = [],
            });
        _files.Serve(CollectionUrl, await collectionZip.Path.ReadAllBytesAsync());
        var loadout = await CreateLoadout();
        var slug = CollectionSlug.From("fetched-" + Guid.NewGuid().ToString("N"));

        var act = async () => await Downloader.GetOrAddRevision(slug, RevisionNumber.From(1), loadout.LoadoutId, CancellationToken.None);

        await act.Should().ThrowAsync<RevisionQueryReached>();
        var file = NexusModsCollectionLibraryFile.All(Connection.Db).Single(f => f.CollectionSlug == slug);
        var item = file.AsLibraryFile().AsLibraryItem();
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([GameInstallation.Game.GameId]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public void CollectionAcquisition_IsTheTargetLoadoutsGame()
    {
        var loadout = CreateLoadout().GetAwaiter().GetResult();

        new CollectionAcquisition(loadout.LoadoutId).Destination(Connection.Db)
            .Should().Be(Destination.Of(GameInstallation.Game.GameId));
    }
}
