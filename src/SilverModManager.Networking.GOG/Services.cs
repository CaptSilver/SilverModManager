using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GOG;
using NexusMods.Abstractions.GOG.Values;
using NexusMods.Networking.GOG.CLI;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.GOG.Migration;
using NexusMods.Networking.GOG.Models.Legacy;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Networking.GOG;

public static class Services
{
    public static IServiceCollection AddGOG(this IServiceCollection services)
    {
        services.AddGOGVerbs();
        services.AddSingleton<IClient, Client>();

        // The model stays registered so the migration step can find and excise the row older versions wrote.
        services.AddAuthInfoModel();

        services
            .AddSecretKeys(GogSecretKeys.All)
            .AddSingleton<ISecretMigrationStep, GogAuthMigration>()
            .AddSingleton<GogCredentialCache>()
            .AddSingleton<ISecretPrimable>(sp => sp.GetRequiredService<GogCredentialCache>());
        services.AddOptionParser(s => ProductId.From(ulong.Parse(s)));
        return services;
    }
}
