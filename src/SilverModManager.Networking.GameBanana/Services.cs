using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Downloads;

namespace SilverModManager.Networking.GameBanana;

/// <summary>
/// Service registration.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers an implementation of <see cref="IGameBananaApi"/>.
    /// </summary>
    public static IServiceCollection AddGameBanana(this IServiceCollection serviceCollection)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.TryAddSingleton<HostThrottle>();
        serviceCollection.TryAddSingleton(GameBananaSettings.Default);
        return serviceCollection.AddSingleton<IGameBananaApi>(provider => new GameBananaApi(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<HostThrottle>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<GameBananaSettings>()));
    }
}
