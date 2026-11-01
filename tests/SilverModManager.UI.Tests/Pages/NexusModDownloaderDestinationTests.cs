using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The save-to-library step records a downloaded file for the loadout it was raised for. The Nexus Mods API and the
/// HTTP client are fakes, so nothing reaches the network.
/// </summary>
public class NexusModDownloaderDestinationTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly NexusModsGameId SharedNexusId = NexusModsGameId.From(1704);

    private sealed class FakeMappingCache : IGameDomainToGameIdMappingCache
    {
        public GameDomain GetDomain(NexusModsGameId id) => GameDomain.From("skyrimspecialedition");
        public NexusModsGameId GetId(GameDomain domain) => SharedNexusId;
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

    [Fact]
    public async Task Download_RecordsTheLoadoutsGame_NotAGameOfTheSharedNexusId()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Save to library");
        var loadoutsGame = loadout.InstallationInstance.Game.GameId;
        var number = (uint)Random.Shared.Next(100_000, 900_000);
        var modId = ModId.From(number);
        var fileId = FileId.From(number);

        using (var tx = Connection.BeginTransaction())
        {
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(modId, SharedNexusId),
                Name = "a mod",
                GameDomain = GameDomain.From("skyrimspecialedition"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
            _ = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(fileId, SharedNexusId),
                Name = "mod.zip",
                Version = "1",
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = page,
            };
            await tx.Commit();
        }

        var api = Substitute.For<INexusApiClient>();
        api.DownloadLinksAsync("skyrimspecialedition", modId, fileId, Arg.Any<CancellationToken>()).Returns(new Response<DownloadLink[]>
        {
            Data = [new DownloadLink { Name = "fake", _ShortName = "fake", Uri = new Uri("https://files.test/mod.zip") }],
            Metadata = new ResponseMetadata(),
            StatusCode = HttpStatusCode.OK,
        });
        var login = Substitute.For<ILoginManager>();
        login.IsPremium.Returns(true);
        var client = new HttpClient(new FixedBodyHandler([1, 2, 3, .. BitConverter.GetBytes(number)]));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var overrides = new ServiceCollection()
            .AddSingleton(api)
            .AddSingleton(Substitute.For<IGraphQlClient>())
            .AddSingleton<IGameDomainToGameIdMappingCache>(new FakeMappingCache())
            .AddSingleton(factory)
            .AddSingleton(client)
            .AddSingleton(login)
            .BuildServiceProvider();
        var overlay = new OverlayProvider(Provider, overrides);
        var library = new NexusModsLibrary(overlay);
        var withLibrary = new OverlayProvider(overlay, new ServiceCollection().AddSingleton(library).BuildServiceProvider());

        var result = await new NexusModDownloader(withLibrary).DownloadAsync(
            SharedNexusId, loadout.LoadoutId, new ModToSave("mod", "a mod", "1", number, number), CancellationToken.None);

        result.Should().Be(ModDownloadResult.Added);
        var files = NexusModsFileMetadata.FindByUid(Connection.Db, new FileUid(fileId, SharedNexusId)).Single();
        var item = NexusModsLibraryItem.FindByFileMetadata(Connection.Db, files).Should().ContainSingle().Subject.AsLibraryItem();
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([loadoutsGame]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }
}
