using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.CLI;
using SilverModManager.CLI.Types.IpcHandlers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using NSubstitute;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The nxm handler with fakes for the Nexus Mods API and the HTTP client, so no test reaches the network. The
/// library already holds file 2 of mod 1 in the skyrim domain and no loadout exists for that game.
/// </summary>
public class NxmModUrlTests
{
    private static readonly NexusModsGameId SkyrimId = NexusModsGameId.From(110);

    private readonly IServiceProvider _host;
    private readonly IConnection _connection;

    public NxmModUrlTests(IServiceProvider host, IConnection connection)
    {
        _host = host;
        _connection = connection;
    }

    private sealed class FakeMappingCache : IGameDomainToGameIdMappingCache
    {
        public GameDomain GetDomain(NexusModsGameId id) => GameDomain.From("skyrim");
        public NexusModsGameId GetId(GameDomain domain) => SkyrimId;
    }

    private sealed class ExpectingDownloads(bool expected) : IExpectedNexusDownloads
    {
        public List<(GameDomain, ModId, FileId)> Asked { get; } = [];

        public bool IsExpected(GameDomain domain, ModId modId, FileId fileId)
        {
            Asked.Add((domain, modId, fileId));
            return expected;
        }
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

    /// <summary>Services the handler and the library code behind it ask for: the host's, with the network ones replaced.</summary>
    private sealed class OverlayProvider(IServiceProvider host, ServiceProvider overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType) => overrides.GetService(serviceType) ?? host.GetService(serviceType);
    }

    private sealed record Handler(NxmIpcProtocolHandler Instance, IEventBus Events, string Link);

    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 1000;

    /// <summary>
    /// A handler whose Nexus Mods library, API and HTTP client are fakes. The database holds the mod page and file
    /// metadata for the link, and a library item for the file when <paramref name="libraryHoldsFile"/> is set.
    /// </summary>
    private async Task<Handler> CreateHandler(IExpectedNexusDownloads? expectations, bool libraryHoldsFile)
    {
        var id = Interlocked.Increment(ref _nextId);
        var modId = ModId.From(id);
        var fileId = FileId.From(id);

        var events = Substitute.For<IEventBus>();

        var api = Substitute.For<INexusApiClient>();
        api.DownloadLinksAsync("skyrim", modId, fileId, Arg.Any<CancellationToken>()).Returns(new Response<DownloadLink[]>
        {
            Data = [new DownloadLink { Name = "fake", _ShortName = "fake", Uri = new Uri("https://files.test/skyrim.7z") }],
            Metadata = new ResponseMetadata(),
            StatusCode = HttpStatusCode.OK,
        });

        var login = Substitute.For<ILoginManager>();
        login.GetIsUserLoggedInAsync(Arg.Any<CancellationToken>()).Returns(true);

        // NexusModsLibrary and the HttpDownloadJob it creates take their dependencies from the provider they are built with.
        var libraryServices = new ServiceCollection()
            .AddSingleton(api)
            .AddSingleton(Substitute.For<IGraphQlClient>())
            .AddSingleton<IGameDomainToGameIdMappingCache>(new FakeMappingCache())
            .AddSingleton(new HttpClient(new FixedBodyHandler([1, 2, 3, 4, 5])))
            .BuildServiceProvider();
        var library = new NexusModsLibrary(new OverlayProvider(_host, libraryServices));

        var handlerServices = new ServiceCollection().AddSingleton(library).AddSingleton(events);
        if (expectations is not null) handlerServices.AddSingleton(expectations);
        var provider = new OverlayProvider(_host, handlerServices.BuildServiceProvider());

        await AddMetadata(modId, fileId, libraryHoldsFile);

        var handler = new NxmIpcProtocolHandler(provider, NullLogger<NxmIpcProtocolHandler>.Instance, null!, null!, new FakeMappingCache(), login);
        return new Handler(handler, events, $"nxm://skyrim/mods/{id}/files/{id}");
    }

    private async Task AddMetadata(ModId modId, FileId fileId, bool libraryHoldsFile)
    {
        using var tx = _connection.BeginTransaction();
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(modId, SkyrimId),
            Name = "a mod",
            GameDomain = GameDomain.From("skyrim"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(fileId, SkyrimId),
            Name = "skyrim.7z",
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        if (libraryHoldsFile)
        {
            _ = new LibraryFile.New(tx, out var id)
            {
                FileName = "skyrim.7z",
                Hash = NexusMods.Hashing.xxHash3.Hash.From(fileId.Value),
                Size = Size.FromLong(5),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "already here" },
            };
            tx.Add(id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
            tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        }
        await tx.Commit();
    }

    [Fact]
    public async Task AnExpectedFileIsDownloadedEvenThoughTheLibraryHoldsItAndNoLoadoutExists()
    {
        var expectations = new ExpectingDownloads(expected: true);
        var handler = await CreateHandler(expectations, libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        var asked = expectations.Asked.Should().ContainSingle().Subject;
        asked.Item1.Should().Be(GameDomain.From("skyrim"));
        asked.Item2.Value.Should().Be(asked.Item3.Value);
        handler.Events.Received(1).Send(Arg.Any<CliMessages.ModDownloadStarted>());
        handler.Events.Received(1).Send(Arg.Any<CliMessages.ModDownloadSucceeded>());
        handler.Events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadFailed>());
    }

    [Fact]
    public async Task AFileTheLibraryHoldsIsRefusedWhenNothingExpectsIt()
    {
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.AlreadyExists));
        handler.Events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadStarted>());
    }

    [Fact]
    public async Task AFileForAGameWithoutALoadoutIsRefusedWhenNothingExpectsIt()
    {
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: false);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.GameNotManaged));
    }

    [Fact]
    public async Task WithNoExpectationsRegisteredTheLinkIsTreatedAsUnexpected()
    {
        var handler = await CreateHandler(expectations: null, libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.AlreadyExists));
    }
}
