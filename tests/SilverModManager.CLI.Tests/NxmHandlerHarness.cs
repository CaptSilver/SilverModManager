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
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using NSubstitute;

namespace SilverModManager.CLI.Tests;

/// <summary>A fake Nexus Mods domain table with one game in it.</summary>
internal sealed class FakeMappingCache(GameDomain domain, NexusModsGameId id) : IGameDomainToGameIdMappingCache
{
    public GameDomain GetDomain(NexusModsGameId requested) => domain;
    public NexusModsGameId GetId(GameDomain requested) => id;
}

/// <summary>What the nxm handler was told the app expects, and for which game.</summary>
internal sealed class ExpectingDownloads(bool expected, GameId? game = null) : IExpectedNexusDownloads
{
    public List<(GameDomain, ModId, FileId)> Asked { get; } = [];

    public bool IsExpected(GameDomain domain, ModId modId, FileId fileId)
    {
        Asked.Add((domain, modId, fileId));
        return expected;
    }

    public bool TryGetExpectedGame(GameDomain domain, ModId modId, FileId fileId, out GameId expectedGame)
    {
        expectedGame = game.GetValueOrDefault();
        return expected && game is not null;
    }
}

/// <summary>
/// The nxm handler with fakes for the Nexus Mods API and the HTTP client, so no test reaches the network. The
/// database holds the mod page and file metadata for each link a handler is created for.
/// </summary>
internal sealed class NxmHandlerHarness(IServiceProvider host, IConnection connection, GameDomain domain, NexusModsGameId nexusId)
{
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

    internal sealed record Handler(NxmIpcProtocolHandler Instance, IEventBus Events, string Link, EntityId? HeldItem);

    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 1000;

    /// <summary>
    /// A handler whose Nexus Mods library, API and HTTP client are fakes. The database holds the mod page and file
    /// metadata for the link, and a library item for the file when <paramref name="libraryHoldsFile"/> is set.
    /// </summary>
    public async Task<Handler> CreateHandler(IExpectedNexusDownloads? expectations, bool libraryHoldsFile)
    {
        var id = Interlocked.Increment(ref _nextId);
        var modId = ModId.From(id);
        var fileId = FileId.From(id);

        var events = Substitute.For<IEventBus>();

        var api = Substitute.For<INexusApiClient>();
        api.DownloadLinksAsync(domain.ToString(), modId, fileId, Arg.Any<CancellationToken>()).Returns(new Response<DownloadLink[]>
        {
            Data = [new DownloadLink { Name = "fake", _ShortName = "fake", Uri = new Uri("https://files.test/file.7z") }],
            Metadata = new ResponseMetadata(),
            StatusCode = HttpStatusCode.OK,
        });

        var login = Substitute.For<ILoginManager>();
        login.GetIsUserLoggedInAsync(Arg.Any<CancellationToken>()).Returns(true);

        // NexusModsLibrary and the HttpDownloadJob it creates take their dependencies from the provider they are built with.
        var libraryServices = new ServiceCollection()
            .AddSingleton(api)
            .AddSingleton(Substitute.For<IGraphQlClient>())
            .AddSingleton<IGameDomainToGameIdMappingCache>(new FakeMappingCache(domain, nexusId))
            .AddSingleton(new HttpClient(new FixedBodyHandler([1, 2, 3, 4, 5])))
            .BuildServiceProvider();
        var library = new NexusModsLibrary(new OverlayProvider(host, libraryServices));

        var handlerServices = new ServiceCollection().AddSingleton(library).AddSingleton(events);
        if (expectations is not null) handlerServices.AddSingleton(expectations);
        var provider = new OverlayProvider(host, handlerServices.BuildServiceProvider());

        var held = await AddMetadata(modId, fileId, libraryHoldsFile);

        var handler = new NxmIpcProtocolHandler(provider, NullLogger<NxmIpcProtocolHandler>.Instance, null!, null!, new FakeMappingCache(domain, nexusId), login);
        return new Handler(handler, events, $"nxm://{domain}/mods/{id}/files/{id}", held);
    }

    private async Task<EntityId?> AddMetadata(ModId modId, FileId fileId, bool libraryHoldsFile)
    {
        using var tx = connection.BeginTransaction();
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(modId, nexusId),
            Name = "a mod",
            GameDomain = domain,
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(fileId, nexusId),
            Name = "skyrim.7z",
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        EntityId? held = null;
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
            held = id;
        }
        var result = await tx.Commit();
        return held is { } item ? result[item] : null;
    }
}
