using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusMods.Games.FromSoft.Saves;

public static class Services
{
    /// <summary>Registers what the saves page of Elden Ring uses.</summary>
    public static IServiceCollection AddEldenRingSaves(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services.AddSingleton<EldenRingSaves>();
    }
}
