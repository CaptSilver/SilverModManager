using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Service registration.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers an implementation of <see cref="IThunderstoreApi"/>.
    /// </summary>
    public static IServiceCollection AddThunderstore(this IServiceCollection serviceCollection)
    {
        return serviceCollection.AddSingleton<IThunderstoreApi>(p => new ThunderstoreApi(p.GetRequiredService<HttpClient>()));
    }
}
