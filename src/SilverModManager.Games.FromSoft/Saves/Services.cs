using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SilverModManager.Games.FromSoft.Saves;

public static class Services
{
    /// <summary>Registers what the saves page of the FromSoft games uses.</summary>
    public static IServiceCollection AddFromSoftSaves(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services.AddSingleton<FromSoftSaves>();
    }
}
