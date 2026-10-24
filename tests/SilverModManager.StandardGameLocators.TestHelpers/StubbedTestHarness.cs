using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.StandardGameLocators.TestHelpers;

public static class StubbedTestHarnessExtensions
{
    public static IServiceCollection AddUniversalGameLocator<TGame>(
        this IServiceCollection services,
        Version version,
        Dictionary<RelativePath, byte[]>? gameFiles = null,
        GameStore[]? stores = null,
        Func<IServiceProvider, IFileSystem>? locationsFileSystem = null,
        OSPlatform? platform = null)
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
                    locationsFileSystem?.Invoke(s))
                {
                    Platform = platform ?? OSPlatform.Linux,
                });

        return services;
    }
}
