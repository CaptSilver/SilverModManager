using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.Networking.EpicGameStore;

public static class Services
{
    public static IServiceCollection AddEpicGameStore(this IServiceCollection s)
    {
        s.AddSingleton<EgDataClient>();
        s.AddEGSVerbs();
        return s;
    }
}
