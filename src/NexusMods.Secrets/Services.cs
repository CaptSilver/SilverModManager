using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.Secrets.Chain;
using NexusMods.Secrets.File;
using NexusMods.Secrets.Overlay;

namespace NexusMods.Secrets;

public static class Services
{
    /// <summary>
    /// The environment variable that names a secrets file for processes started without <c>--secrets-file</c>.
    /// </summary>
    public const string SecretsFileEnvironmentVariable = "SILVERMM_SECRETS_FILE";

    // The key classes that own these names live in projects this one cannot reference.
    private static readonly IReadOnlyDictionary<SecretKey, string> Aliases = new Dictionary<SecretKey, string>
    {
        [new SecretKey("nexus.api-key")] = "NEXUS_API_KEY",
        [new SecretKey("github.token")] = "GITHUB_TOKEN",
    };

    /// <summary>
    /// Registers the secret store: the environment and secrets file overlay over the keyring-then-file chain,
    /// the file alone, or memory (the test host choice), depending on <paramref name="options"/> and the user's setting.
    /// </summary>
    public static IServiceCollection AddSecrets(this IServiceCollection services, SecretStoreOptions options)
    {
        SecretKeyRegistry.ThrowOnSharedEnvironmentVariable(services.RegisteredSecretKeys());

        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.TryAddSingleton<ISecretKeyRegistry>(sp => new SecretKeyRegistry(sp.GetServices<SecretKeyRegistration>().SelectMany(r => r.Keys)));

        return services
            .AddSingleton(options)
            .AddSettings<SecretsSettings>()
            .AddSingleton<FileSecretStore>()
            .AddSingleton(sp => new InnerChain(BuildInner(sp, options)))
            .AddSingleton<IPersistentSecretStore>(sp => new PersistentSecretStore(sp.GetRequiredService<InnerChain>().Store))
            .AddSingleton<ISecretStore>(sp => BuildOverlay(sp, options))
            .AddSingleton<SecretMigrationRunner>();
    }

    private static ISecretStore BuildInner(IServiceProvider sp, SecretStoreOptions options)
    {
        var backend = options.Backend ?? sp.GetRequiredService<ISettingsManager>().Get<SecretsSettings>().Backend;
        return backend switch
        {
            SecretBackendChoice.Memory => new InMemorySecretStore(),
            SecretBackendChoice.FileOnly => sp.GetRequiredService<FileSecretStore>(),
            SecretBackendChoice.Auto => new FallbackSecretStore(
                sp.GetService<ISecretServiceStore>(),
                sp.GetRequiredService<FileSecretStore>(),
                options.Mode,
                sp.GetRequiredService<ILogger<FallbackSecretStore>>()),
            _ => throw new ArgumentOutOfRangeException(nameof(options), backend, "Unknown secret backend."),
        };
    }

    private static OverlaySecretStore BuildOverlay(IServiceProvider sp, SecretStoreOptions options)
    {
        var environment = sp.GetRequiredService<IEnvironmentReader>();
        // The option wins over the variable, as --secrets-file wins over SILVERMM_SECRETS_FILE.
        var path = options.SecretsFile?.ToString() ?? environment.Get(SecretsFileEnvironmentVariable);
        var file = string.IsNullOrEmpty(path) ? null : new SecretsFileReader(path, sp.GetRequiredService<ILogger<SecretsFileReader>>());

        return new OverlaySecretStore(
            sp.GetRequiredService<InnerChain>().Store,
            environment,
            file,
            Aliases,
            sp.GetRequiredService<ISecretKeyRegistry>().Keys);
    }

    /// <summary>
    /// The chain under the overlay, resolved once so the overlay and <see cref="IPersistentSecretStore"/> share it.
    /// </summary>
    private sealed record InnerChain(ISecretStore Store);
}
