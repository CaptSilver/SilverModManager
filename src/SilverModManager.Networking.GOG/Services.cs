using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.GOG;
using SilverModManager.Abstractions.GOG.Values;
using SilverModManager.Networking.GOG.CLI;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.GOG.Migration;
using SilverModManager.Networking.GOG.Models.Legacy;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Networking.GOG;

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
