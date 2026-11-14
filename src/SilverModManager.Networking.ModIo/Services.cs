using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.ModIo;

/// <summary>
/// Service registration.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers the mod.io client and the account row for the user's access token.
    /// </summary>
    public static IServiceCollection AddModIo(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<HostThrottle>();
        // The window registers its overlay prompt before this runs; a host without one refuses and names the row.
        services.TryAddSingleton<ISecretPrompt, HeadlessSecretPrompt>();
        services.AddSecretKeys(ModIoSecretKeys.Token);
        services.AddSettings<ModIoSettings>();
        services.AddSingleton<IModIoApi>(provider => new ModIoApi(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<HostThrottle>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ISettingsManager>() is { } settings && settings.Configs.ContainsKey(typeof(ModIoSettings)) ? settings.Get<ModIoSettings>() : ModIoSettings.Default));
        services.AddSingleton<ModIoAccount>();
        services.AddSingleton<ISecretPrimable>(sp => sp.GetRequiredService<ModIoAccount>());
        return services;
    }
}
