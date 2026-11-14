using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Networking.Steam.Workshop.DTOs;

namespace SilverModManager.Library.Workshop;

/// <summary>
/// <see cref="ISteamWorkshopApi"/> that looks the real one up when a request is made. The library registers the catalog,
/// the mod source and the verbs in every host that has a library, including hosts that never added the Workshop client;
/// those can still export and verify a lock, and only a request that needs Steam finds it missing.
/// </summary>
internal sealed class DeferredSteamWorkshopApi(IServiceProvider provider) : ISteamWorkshopApi
{
    public Task<IReadOnlyList<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids, CancellationToken ct)
        => provider.GetRequiredService<ISteamWorkshopApi>().Details(ids, ct);
}

/// <summary>
/// The Workshop client the view and the catalog are built with, looked up when first used.
/// </summary>
internal static class SteamWorkshopClients
{
    public static ISteamWorkshopApi Api(IServiceProvider provider) => new DeferredSteamWorkshopApi(provider);
}
