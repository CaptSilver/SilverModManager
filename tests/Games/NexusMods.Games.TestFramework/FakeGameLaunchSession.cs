using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.TestFramework;

/// <summary>
/// A launch session registry whose answer the test chooses, so no test needs a real launch lock.
/// </summary>
public class FakeGameLaunchSession : IGameLaunchSession
{
    /// <summary>
    /// What <see cref="IsRunning"/> returns for every installation; nothing is running by default.
    /// </summary>
    public bool Running { get; set; }

    /// <inheritdoc />
    public bool IsRunning(GameInstallation installation) => Running;
}

public static class FakeGameLaunchSessionExtensions
{
    /// <summary>
    /// Registers a <see cref="FakeGameLaunchSession"/> as the launch session registry, unless one is already
    /// registered. The fake itself is registered too, so tests can switch it on.
    /// </summary>
    public static IServiceCollection AddFakeGameLaunchSession(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeGameLaunchSession>();
        services.TryAddSingleton<IGameLaunchSession>(s => s.GetRequiredService<FakeGameLaunchSession>());
        return services;
    }
}
