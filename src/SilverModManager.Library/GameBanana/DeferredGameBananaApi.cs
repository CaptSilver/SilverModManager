using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Library.GameBanana;

/// <summary>
/// <see cref="IGameBananaApi"/> that looks the real one up when a request is made. The library registers the catalog,
/// the mod source and the verbs in every host that has a library, including hosts that never added the GameBanana
/// client; those can still list, export and verify, and only a request that needs the site finds it missing.
/// </summary>
internal sealed class DeferredGameBananaApi(IServiceProvider provider) : IGameBananaApi
{
    private IGameBananaApi Api => provider.GetRequiredService<IGameBananaApi>();

    public Task<GameBananaListing> Subfeed(int gameId, int page, int perPage, CancellationToken ct) => Api.Subfeed(gameId, page, perPage, ct);

    public Task<GameBananaListing> Index(int gameId, GameBananaSection section, string sort, int page, int perPage, CancellationToken ct)
        => Api.Index(gameId, section, sort, page, perPage, ct);

    public Task<GameBananaListing> Search(int gameId, string text, int page, CancellationToken ct) => Api.Search(gameId, text, page, ct);

    public Task<GameBananaProfile> Profile(long modId, CancellationToken ct) => Api.Profile(modId, ct);
}

/// <summary>
/// The GameBanana client and HTTP client the catalog and the mod source are built with, looked up when first used.
/// </summary>
internal static class GameBananaClients
{
    public static IGameBananaApi Api(IServiceProvider provider) => new DeferredGameBananaApi(provider);

    public static Func<HttpClient> Http(IServiceProvider provider) => () => provider.GetRequiredService<HttpClient>();
}
