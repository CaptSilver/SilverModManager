using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.Secrets.Chain;
using SilverModManager.Secrets.File;
using SilverModManager.Secrets.Migration;
using SilverModManager.Secrets.Overlay;
using SilverModManager.Secrets.Verbs;

namespace SilverModManager.Secrets;

public static class Services
{
    /// <summary>
    /// The environment variable that names a secrets file for processes started without <c>--secrets-file</c>.
    /// </summary>
    public const string SecretsFileEnvironmentVariable = "SILVERMM_SECRETS_FILE";

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
            .AddSingleton<SecretMigrationRunner>()
            .AddSecretVerbs();
    }

    private static ISecretStore BuildInner(IServiceProvider sp, SecretStoreOptions options)
    {
        var backend = ResolveBackend(options, () => sp.GetRequiredService<ISettingsManager>().Get<SecretsSettings>().Backend);
        return backend switch
        {
            SecretBackendChoice.Memory => new InMemorySecretStore(),
            SecretBackendChoice.FileOnly => sp.GetRequiredService<FileSecretStore>(),
            SecretBackendChoice.Auto => new FallbackSecretStore(
                sp.GetService<ISecretServiceStore>(),
                sp.GetRequiredService<FileSecretStore>(),
                options.Mode,
                sp.GetRequiredService<ILogger<FallbackSecretStore>>()),
            _ => throw SecretBackendRefusedException.Unknown(backend),
        };
    }

    /// <summary>
    /// The options override wins and may name any backend, the test hosts' memory store included. The user's setting
    /// may only name what settings offer: a settings file that says Memory would drop every credential at exit.
    /// </summary>
    private static SecretBackendChoice ResolveBackend(SecretStoreOptions options, Func<SecretBackendChoice> setting)
    {
        if (options.Backend is { } overridden)
        {
            if (!Enum.IsDefined(overridden)) throw SecretBackendRefusedException.Unknown(overridden);
            return overridden;
        }

        var configured = setting();
        if (!SecretsSettings.OfferedBackends.Contains(configured)) throw SecretBackendRefusedException.NotOffered(configured);
        return configured;
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
            WellKnownSecrets.EnvironmentAliases,
            sp.GetRequiredService<ISecretKeyRegistry>().Keys);
    }

    /// <summary>
    /// The chain under the overlay, resolved once so the overlay and <see cref="IPersistentSecretStore"/> share it.
    /// </summary>
    private sealed record InnerChain(ISecretStore Store);
}
