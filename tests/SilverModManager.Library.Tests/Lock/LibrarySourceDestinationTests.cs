using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Backend;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Sources;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Library.Tests.Lock;

/// <summary>
/// A lock re-download is for the game the lock is imported into. The Nexus Mods source runs against a fake API and
/// HTTP client, so no test reaches the network.
/// </summary>
public class LibrarySourceDestinationTests(IServiceProvider host, IConnection connection)
{
    private static readonly NexusModsGameId NexusGame = LockFixtures.GameId;

    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 5000;

    private sealed class FakeMappingCache : IGameDomainToGameIdMappingCache
    {
        public GameDomain GetDomain(NexusModsGameId id) => GameDomain.From("eldenring");
        public NexusModsGameId GetId(GameDomain domain) => NexusGame;
    }

    private sealed class FixedBodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(body);
            response.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class OverlayProvider(IServiceProvider host, ServiceProvider overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType) => overrides.GetService(serviceType) ?? host.GetService(serviceType);
    }

    /// <summary>A Nexus Mods source whose library, API and HTTP client are fakes, and the entry for a file of its own.</summary>
    private async Task<(NexusLibrarySource Source, LibrarySourceEntry Entry)> CreateNexusSource()
    {
        var id = Interlocked.Increment(ref _nextId);
        var modId = ModId.From(id);
        var fileId = FileId.From(id);

        var api = Substitute.For<INexusApiClient>();
        api.DownloadLinksAsync("eldenring", modId, fileId, Arg.Any<CancellationToken>()).Returns(new Response<DownloadLink[]>
        {
            Data = [new DownloadLink { Name = "fake", _ShortName = "fake", Uri = new Uri("https://files.test/mod.zip") }],
            Metadata = new ResponseMetadata(),
            StatusCode = HttpStatusCode.OK,
        });

        var modFile = Substitute.For<IModFile>();
        modFile.ModId.Returns((int)id);
        var graphQl = Substitute.For<IGraphQlClient>();
        graphQl.QueryModFile(fileId, NexusGame, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new GraphQlResult<IModFile, NotFound>(modFile)));

        var client = new HttpClient(new FixedBodyHandler([1, 2, 3, .. BitConverter.GetBytes(id)]));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var overrides = new ServiceCollection()
            .AddSingleton(factory)
            .AddSingleton(new System.Text.Json.JsonSerializerOptions())
            .AddSingleton(api)
            .AddSingleton(graphQl)
            .AddSingleton<IGameDomainToGameIdMappingCache>(new FakeMappingCache())
            .AddSingleton(client)
            .BuildServiceProvider();
        var provider = new OverlayProvider(host, overrides);
        var library = new NexusModsLibrary(provider);
        var services = new ServiceCollection().AddSingleton(library).BuildServiceProvider();

        // The mod page and file metadata are already known, so the library does not ask GraphQL for them.
        using (var tx = connection.BeginTransaction())
        {
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(modId, NexusGame),
                Name = "a mod",
                GameDomain = GameDomain.From("eldenring"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
            _ = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(fileId, NexusGame),
                Name = "mod.zip",
                Version = "1",
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = page,
            };
            await tx.Commit();
        }

        var digests = new LibraryFileDigests(Substitute.For<SilverModManager.Sdk.FileStore.IFileStore>(), connection);
        var source = new NexusLibrarySource(new OverlayProvider(provider, services), digests);
        var entry = new LibrarySourceEntry("mod", "1", fileId.Value.ToString(), "mod.zip", LockFixtures.DigestOf("x").ToString(), LockSources.Nexus);
        return (source, entry);
    }

    private LibrarySourceContext Context(IGameData? game) => new(connection, game, NexusGame);

    private LibraryItem.ReadOnly Reload(LibraryFile.ReadOnly file) => LibraryItem.Load(connection.Db, file.Id);

    [Fact]
    public async Task NexusLibrarySource_Download_RecordsTheContextsGame()
    {
        var (source, entry) = await CreateNexusSource();
        var game = new StubbedGame(host);

        var file = await source.Download(entry, LockFixtures.DigestOf("x"), Context(game), TestContext.Current.CancellationToken);

        var item = Reload(file);
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([StubbedGame.GameId]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public async Task NexusLibrarySource_Download_WithoutAGame_MarksUnknown()
    {
        var (source, entry) = await CreateNexusSource();

        var file = await source.Download(entry, LockFixtures.DigestOf("x"), Context(game: null), TestContext.Current.CancellationToken);

        var item = Reload(file);
        LibraryDestinations.GamesOf(item).Should().BeEmpty();
        item.Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
    }

    [Fact]
    public void LockAcquisition_NamesTheContextsGame_OrNone()
    {
        new LockAcquisition(Context(new StubbedGame(host))).Destination(connection.Db)
            .Should().Be(Destination.Of(StubbedGame.GameId));
        new LockAcquisition(Context(game: null)).Destination(connection.Db).IsUnknown.Should().BeTrue();
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
