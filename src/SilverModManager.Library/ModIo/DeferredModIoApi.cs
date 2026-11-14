using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Networking.ModIo;

namespace SilverModManager.Library.ModIo;

/// <summary>
/// <see cref="IModIoApi"/> that looks the real one up when a request is made. The library registers the catalog, the mod
/// source and the verbs in every host that has a library, including hosts that never added the mod.io client; those can
/// still list, export and verify, and only a request that needs the service finds it missing.
/// </summary>
internal sealed class DeferredModIoApi(IServiceProvider provider) : IModIoApi
{
    private IModIoApi Api => provider.GetRequiredService<IModIoApi>();

    public Task<ModIoPage<ModIoMod>> Mods(uint gameId, ModIoQuery query, CancellationToken ct) => Api.Mods(gameId, query, ct);

    public Task<ModIoMod?> Mod(uint gameId, uint modId, CancellationToken ct) => Api.Mod(gameId, modId, ct);

    public Task<IReadOnlyList<ModIoFile>> Files(uint gameId, uint modId, CancellationToken ct) => Api.Files(gameId, modId, ct);

    public Task Subscribe(uint gameId, uint modId, bool includeDependencies, CancellationToken ct) => Api.Subscribe(gameId, modId, includeDependencies, ct);

    public Task Unsubscribe(uint gameId, uint modId, CancellationToken ct) => Api.Unsubscribe(gameId, modId, ct);

    public Task<IReadOnlyList<ModIoMod>> Subscribed(uint gameId, CancellationToken ct) => Api.Subscribed(gameId, ct);

    public Task<ModIoUser> Me(CancellationToken ct) => Api.Me(ct);

    public Task<ModIoUser> Me(string token, CancellationToken ct) => Api.Me(token, ct);
}

/// <summary>
/// The mod.io client and HTTP client the catalog and the mod source are built with, looked up when first used.
/// </summary>
internal static class ModIoClients
{
    public static IModIoApi Api(IServiceProvider provider) => new DeferredModIoApi(provider);

    public static Func<HttpClient> Http(IServiceProvider provider) => () => provider.GetRequiredService<HttpClient>();
}
