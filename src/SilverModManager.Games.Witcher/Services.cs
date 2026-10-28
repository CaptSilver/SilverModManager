using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Witcher.Witcher1;
using SilverModManager.Games.Witcher.Witcher2;
using SilverModManager.Games.Witcher.Witcher3;

namespace SilverModManager.Games.Witcher;

public static class Services
{
    public static IServiceCollection AddWitcherGames(this IServiceCollection services)
    {
        return services
            .AddGame<Witcher1Game>()
            .AddGame<Witcher2Game>()
            .AddGame<Witcher3Game>();
    }
}
