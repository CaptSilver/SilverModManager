using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.StandardGameLocators.TestHelpers;

public static class StubbedTestHarnessExtensions
{
    public static IServiceCollection AddUniversalGameLocator<TGame>(
        this IServiceCollection services,
        Version version,
        Dictionary<RelativePath, byte[]>? gameFiles = null,
        GameStore[]? stores = null,
        Func<IServiceProvider, IFileSystem>? locationsFileSystem = null)
        where TGame : IGame
    {
        services
            .AddSingleton<IGameLocator, UniversalStubbedGameLocator<TGame>>(s =>
                new UniversalStubbedGameLocator<TGame>(
                    s,
                    s.GetRequiredService<IFileSystem>(),
                    s.GetRequiredService<TemporaryFileManager>(),
                    gameFiles,
                    stores,
                    locationsFileSystem?.Invoke(s)));

        return services;
    }

    /// <summary>
    /// Locates the game with <paramref name="gameId"/>, looked up among the registered games when the
    /// locator is created. For a game that has no class to name, such as one built from a definition.
    /// </summary>
    public static IServiceCollection AddUniversalGameLocator(
        this IServiceCollection services,
        GameId gameId,
        Dictionary<RelativePath, byte[]>? gameFiles = null,
        GameStore[]? stores = null,
        Func<IServiceProvider, IFileSystem>? locationsFileSystem = null)
    {
        services.AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator(
            s.GetServices<IGame>().Single(game => game.GameId == gameId),
            gameId.Value.ToString(),
            s.GetRequiredService<IFileSystem>(),
            s.GetRequiredService<TemporaryFileManager>(),
            gameFiles,
            stores,
            locationsFileSystem?.Invoke(s)));

        return services;
    }
}
