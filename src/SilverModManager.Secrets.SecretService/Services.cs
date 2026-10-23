using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk;

namespace SilverModManager.Secrets.SecretService;

public static class Services
{
    /// <summary>
    /// Registers the Secret Service store that the keyring-then-file chain from <c>AddSecrets</c> picks up.
    /// Nothing is registered for the in-memory backend, so test hosts have no Secret Service descriptor at all
    /// and can never reach a keyring through it.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <c>AddSecrets</c> has not run yet.</exception>
    public static IServiceCollection AddSecretServiceBackend(this IServiceCollection services)
    {
        // Read from the descriptors because the choice has to be made now, before any provider exists.
        var options = services.LastOrDefault(d => d.ServiceType == typeof(SecretStoreOptions))?.ImplementationInstance as SecretStoreOptions
            ?? throw new InvalidOperationException("AddSecrets must be called before AddSecretServiceBackend.");

        if (options.Backend == SecretBackendChoice.Memory) return services;

        return services.AddSingleton<ISecretServiceStore>(sp => new SecretServiceStore(
            new TmdsSecretServiceConnection(),
            sp.GetRequiredService<IEnvironmentReader>(),
            sp.GetRequiredService<SecretStoreOptions>(),
            sp.GetRequiredService<ILogger<SecretServiceStore>>()));
    }
}
