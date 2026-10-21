using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft;

public static class Services
{
    /// <summary>
    /// Registers the FromSoft games. Each block takes the lines of one kind of registration, so two changes that
    /// add to different blocks never touch the same line.
    /// </summary>
    public static IServiceCollection AddFromSoftGames(this IServiceCollection services)
    {
        services.AddGame<EldenRingGame>()
            .AddSettings<FromSoftSettings>()
            .AddSingleton<ITool, RunEldenRingGame>();

        // Emitters

        // Launch gates, in run order

        // Registrations owned by other folders

        return services;
    }
}
